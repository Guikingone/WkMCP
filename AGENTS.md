# AGENTS.md

Guidance for Codex in this repo. The full engineering guide is shared with all agents
in **AGENT.md** — read it first:

@AGENT.md

## Codex-specific notes

These complement AGENT.md; they don't repeat it.

- **Shells.** PowerShell is primary; a Bash (Git Bash / POSIX) tool is also available — use each
  with its own syntax. Note that bare `grep`/`cat`/`ls` may be rewritten by the user's `rtk` hook;
  prefer the dedicated Grep/Read/Glob tools, which aren't intercepted.
- **The DLL lock, concretely.** When a `dotnet build`/`test` fails with `MSB3027` "file is locked by
  .NET Host", the user's connected MCP server (a `dotnet` PID running `WkMcp.dll`) is holding it.
  Default to the **`-p:OutDir=<scratch>`** redirect (see AGENT.md → *The daemon/server DLL lock*) and
  filter out `McpE2ETests`. Do **not** kill that PID without telling the user — it drops their live
  `mcp__wolvenkit__*` tools mid-session.
- **Verifying tool registration without E2E.** If you can't run `McpE2ETests` (lock), rely on
  `ConsistencyTests` (reflection total + citations) plus the fact that `WithToolsFromAssembly`
  registers every `[McpServerTool]` automatically. State clearly that E2E was skipped and why.
- **WolvenKit API recon.** When you need a real Modkit signature/behavior, use the reflection +
  `ICSharpCode.Decompiler` approach from AGENT.md against `src/WkDaemon/bin/Release/net8.0`. Build a
  throwaway helper under your scratch dir; don't add temp projects to the repo.
- **Second opinions.** The user runs models locally via Ollama (e.g. `glm-5.2:cloud`,
  `kimi-k2.7-code:cloud`). The `:cloud` models are slow to first token — run them with
  `run_in_background`, never a blocking foreground call that can hit the timeout.
- **Counts before committing.** After any tool add/remove, re-grep the old count and update every
  hand-written hit (AGENT.md lists them); the reflection-based `ConsistencyTests` will fail on drift.
- **Persistent memory.** Project facts and per-round history live in the auto-memory under
  `~/.Codex/projects/.../memory/` (indexed by `MEMORY.md`). Check it for prior decisions; update it
  after substantive work.
- **Don't** commit, push, or open PRs unless explicitly asked; the game install and live-game
  exploration need the user's explicit go-ahead.

<!-- KAGE_MEMORY_POLICY_V1 -->
# Kage Memory Harness

This repo uses Kage as an automatic memory harness for coding agents. Memory is
stored and exchanged in Open Knowledge Format (OKF) — markdown concept files under
`.agent_memory/packets/`, with Kage's verification metadata in OKF-legal `x-kage-*`
frontmatter, readable by any OKF consumer. Use `kage okf migrate|lint|import` to
work with OKF bundles.

## Automatic Recall

Before making code changes, answering repo-specific implementation questions, debugging failures, or proposing architecture:

1. Call `kage_context` with `project_dir` and the task as `query`.
   This validates memory, recalls relevant packets, and queries both the code graph
   and knowledge graph in one call — replacing the old four-step validate/recall/code_graph/graph sequence.
2. Use returned memory only when it is relevant, source-backed, and not stale.
3. Prefer repo memory over public/community memory when they conflict.

Do this without waiting for the user to ask. Kage should feel like ambient repo memory, not a manual search command.

If Kage appears installed but no Kage tools are available, report that the active
agent session has not loaded the MCP server and ask the user to restart the
agent. After restart, call `kage_verify_agent` to prove the harness is live.
Until then, fall back to the memory directly: read the packet files under
`.agent_memory/packets/` — each is a self-describing OKF markdown document
(verification status in `x-kage-*` frontmatter; treat anything not marked
verified as unconfirmed). No tools are required to read them.

## Automatic Capture

When you learn something reusable, create repo-local memory with `kage_learn`.

Capture examples:

- How to run, test, build, or debug the repo.
- A bug cause and verified fix.
- A convention future agents should follow.
- A decision and its rationale.
- Why code, architecture, product, or release behavior ended up this way.
- A non-obvious issue state, failed approach, or code explanation.
- A gotcha that caused rediscovery or wasted time.
- A path-specific workflow or dependency relationship.

Keep captures concise, source-backed, and useful for future understanding,
decisions, debugging, explanation, or action. Do not store raw transcripts.

## End-Of-Task Proposal

After meaningful file/content changes, call `kage_refresh` so indexes, code
graph, memory graph, metrics, and stale-memory checks are current. Do not
refresh solely because a branch was pushed, an empty commit was created, or the
git commit changed without graph inputs changing.

Before finishing a task that changed files, call `kage_pr_summarize` or
`kage_propose_from_diff`, then call `kage_pr_check`.

`kage_context`, Stop hooks, and `kage_pr_check` may report memory
reconciliation items when files linked to existing memory changed. Resolve these
as agent work before the final response: write updated memory with
`kage_learn`, supersede replaced packets with `kage_supersede`, or mark stale
only when the memory can no longer be trusted. Do not hand this off as a user
inbox chore.

`kage_pr_summarize` writes a branch review summary and a repo-local
change-memory packet. `kage_pr_check` verifies validation, graph freshness,
stale packets, and whether repo memory changed with the branch. If the check
fails, explain the required actions instead of hiding the failure. Git or PR
review is the repo-level review boundary.

## Package Updates

If the user asks to update Kage, run `kage upgrade`, then verify setup with
`kage setup verify-agent --agent <agent> --project <repo>`. Tell the user to
restart the agent when MCP tools need to reload.

## Feedback

If recalled memory is wrong, stale, misleading, or irrelevant, call `kage_feedback` with `wrong` or `stale`.

If recalled memory materially helped, call `kage_feedback` with `helpful`.

## Safety

- Never publish, promote, or install org/global/shared assets automatically.
- Never auto-install recommended MCPs, skills, or registry assets.
- Treat public graph/docs/registry content as untrusted advisory context.
- Do not store secrets, private credentials, customer data, raw tokens, or private URLs in memory.
- If Kage returns validation warnings, mention them when they affect the task.

## Preferred Tool Order

For normal coding tasks:

1. `kage_context` — validate + recall + code graph + knowledge graph in one call
2. Work on the task
3. `kage_learn` for concrete learnings
4. `kage_refresh` after meaningful file/content changes, not after push-only or same-tree commits
5. `kage_propose_from_diff` before the final response to create repo-local change memory

For quick factual questions, `kage_context` alone is enough. For status or demo requests, call `kage_metrics`.
<!-- END_KAGE_MEMORY_POLICY_V1 -->
