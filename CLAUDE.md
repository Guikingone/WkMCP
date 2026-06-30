# CLAUDE.md

Guidance for Claude Code in this repo. The full engineering guide is shared with all agents
in **AGENT.md** — read it first:

@AGENT.md

## Claude-Code-specific notes

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
  `~/.claude/projects/.../memory/` (indexed by `MEMORY.md`). Check it for prior decisions; update it
  after substantive work.
- **Don't** commit, push, or open PRs unless explicitly asked; the game install and live-game
  exploration need the user's explicit go-ahead.
