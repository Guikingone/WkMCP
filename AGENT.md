# Agent guide — WkMCP

Engineering guide for AI agents (and humans) working in this repo. Vendor-neutral;
`CLAUDE.md` imports this file. Read it before editing — most mistakes here come from
*not* knowing the conventions below, especially the **tool-count discipline** and the
**daemon DLL lock**.

## What this is

**WkMCP** is an [MCP](https://modelcontextprotocol.io) server that exposes the
WolvenKit 8.18.0 modding toolkit for **Cyberpunk 2077** as tools an LLM agent can call
(read/write game files, TweakDB, pack/install mods, export/import art, lint REDscript,
diagnose installs, and drive a *running* game live). C# / .NET 8, Windows-first.

**Current surface: 168 tools (132 offline + 36 `live_*`), 11 prompts, 4 resources.**
These numbers are computed by reflection (see *Tool-count discipline*) — never trust a
hardcoded number you remember; recompute.

## Architecture — two processes + a live bridge

```
MCP client (Claude) ──stdio/HTTP──► WkMcp (host)  ──IPC (JSON lines)──►  WkDaemon (worker)
                                       │                                    └ links WolvenKit.Modkit/RED4, kraken/Oodle
                                       └──TCP──► CETBridge (Lua mod in a running game)  ← the 36 live_* tools
```

- **`src/WkMcp`** — the MCP host. Speaks MCP JSON-RPC (stdio default, opt-in HTTP).
  Links **no** WolvenKit library. Tools shell out to the daemon via `Cp77ToolsRunner`.
- **`src/WkDaemon`** — a persistent process that links the heavy WolvenKit libraries and
  loads reference data **once** (`HashService` ~6 s), then serves requests in a loop. This
  is why a daemon exists: avoid paying cold-start per call. Its build also **deploys the
  native binaries** (`kraken.dll`, `DirectXTexNet.dll`) — so it **must be built first**.
- **`Cp77ToolsRunner`** (in WkMcp) routes each call to the daemon over pipelined IPC;
  falls back to a `cp77tools` subprocess if the daemon DLL is missing. The host finds the
  daemon via `WKMCP_DAEMON` or the sibling build output.
- **CETBridge** (`live-bridge/CETBridge/`) — a Cyber Engine Tweaks Lua mod the user installs
  in their game; `CetBridge.cs` talks to it over TCP for the `live_*` tools.

## Repo layout

| Path | What |
|---|---|
| `src/WkMcp/` | MCP host. Tool classes (see below), `Cp77ToolsRunner.cs`, `CetBridge.cs`, `Program.cs`, `WolvenKitResources.cs` (the `wkmcp://reference` resource), `WolvenKitPrompts.cs`. |
| `src/WkDaemon/Program.cs` | The whole daemon: verb dispatch + special verbs. ~1100 lines. |
| `src/WkMcp.Tests/` | xUnit. `ConsistencyTests.cs` (counts/citations/annotations), `McpE2ETests.cs` (launches the real server). |
| `live-bridge/CETBridge/` | The in-game Lua mod (`init.lua`, `handlers.lua`). |
| `docs/` | `TOOLS.md` (per-tool reference), `ARCHITECTURE.md`, `HTTP_TRANSPORT.md`, `LIVE_BRIDGE.md`. |
| `native/`, `nexus/`, `dist/`, `tools/`, `dev/` | native libkraken build, Nexus packaging, `.mcpb` bundle output, helper scripts, dev notes. |
| `build-mcpb.ps1` | Builds the Desktop Extension bundle → `dist/wkmcp.mcpb`. |
| `.github/workflows/ci.yml` | Build daemon → build server → test → build+validate `.mcpb` (Windows). |

### Tool classes (where to add a tool)

All five auto-register via `mcp.WithToolsFromAssembly()` in `Program.cs` — **any class with
`[McpServerTool]` methods is picked up automatically**, no manual registration.

- `WolvenKitTools.cs` — base/offline tools (archives, CR2W, TweakDB, import/export, install, projects).
- `ModdingTools.cs` — high-level workflow/intelligence tools. **`partial class ModdingTools`**, split across:
  - `AssetInspectionTools.cs` and `GameplayInspectionTools.cs` (also `partial ModdingTools`).
  - All three count as `ModdingTools` in reflection.
- `SceneTools.cs` — `.scene` tools.
- `ProbeTools.cs` — `game_probe` (its own "diagnostic probe" category).
- `LiveTools.cs` — the 36 `live_*` tools.

## Build & run

**Order matters: daemon first** (it deploys the native binaries the runtime needs).

```powershell
dotnet build src/WkDaemon      # deploys kraken.dll, DirectXTexNet.dll
dotnet build src/WkMcp
dotnet test  src/WkMcp.Tests
```

Run the server (stdio): `dotnet src/WkMcp/bin/Debug/net8.0/WkMcp.dll`.
HTTP mode and security: see `docs/HTTP_TRANSPORT.md`. Env vars (`WKMCP_*`) are documented
in `Cp77ToolsRunner.cs` and `manifest.json` (legacy `WOLVENKIT_*` names still honored).

### ⚠ The daemon/server DLL lock (read this)

A **running MCP server holds a lock on `src/WkMcp/bin/.../WkMcp.dll`** (and the daemon on
`WkDaemon.dll`). A normal `dotnet build` then fails at the *copy-to-bin* step (`MSB3027`,
"file is locked by .NET Host") — even though compile-to-`obj` succeeded. Two ways out:

1. **Redirect the output** (preferred — non-destructive, doesn't touch the live server):
   ```bash
   dotnet build src/WkMcp/WkMcp.csproj -p:OutDir=/path/to/scratch/mcp/
   dotnet test  src/WkMcp.Tests/WkMcp.Tests.csproj -p:OutDir=/path/to/scratch/tests/ \
       --filter "FullyQualifiedName!~McpE2ETests"
   ```
2. **Stop the server process** (a `dotnet`/`.NET Host` PID running `WkMcp.dll`). Only do this
   with the user's awareness — it **disconnects their live `mcp__wolvenkit__*` tools**.

`McpE2ETests` *launches the on-disk DLL*, so it can only pass against a freshly built one —
if the live server holds the lock, skip it (filter out `McpE2ETests`) and say so; the rest
of the suite + reflection covers tool registration.

## Tests — what they enforce

- **`ConsistencyTests`** (the guardrails that catch count drift):
  - The `wkmcp://reference` resource (`WolvenKitResources.BuildReference()`) announces
    `({total} total)` where `total` is computed by reflection over the tool classes.
  - Every backticked tool name cited in prompts/reference **must exist** as a real tool.
  - README contains the literals `{n} tools`, `{n} prompts`, `{n} resources` (n by reflection).
  - Every `[McpServerTool(` has `ReadOnly = / Destructive = / Idempotent =` annotations.
  - **It does NOT check completeness of the README/TOOLS.md *categorical bullet lists*** —
    those can silently miss a tool. Update them by hand (see below).
- **`McpE2ETests`** — handshake + `tools/list` against the real launched server; asserts the
  reflected tool set is actually exposed over the protocol.

## Adding or changing a tool — the checklist

This is the part people get wrong. When you add/rename/remove a tool, do **all** of:

1. **Write the tool** in the right class (auto-registers). Use the standard signature:
   `Cp77ToolsRunner runner` as the first param if it shells out; `CancellationToken ct = default`
   last; optional `IProgress<ProgressNotificationValue>? progress` for long ops. Annotate
   `[McpServerTool(Name="snake_case", ReadOnly=, Destructive=, Idempotent=)]` + `[Description(...)]`
   on the method and each parameter.
2. **Return the structured envelope** — `{ ok, status, summary, produced, warnings, errors, log }`.
   Use the existing helpers: `Err(...)`, `Structured(...)`, `WithSnapshot(outputPath, summary, op)`
   (snapshots the output dir and reports `produced`), `JsonSerializer.Serialize(new {...}, JsonOpts)`.
   Validate inputs and early-return `Err(...)` **before** touching the runner (so the error path
   needs no daemon — and is unit-testable).
3. **Update the counts everywhere** (see *Tool-count discipline*).
4. **Document it** in `docs/TOOLS.md` (entry + param table, in the right numbered section) and add
   it to the relevant "Key tools by task" line in `WolvenKitResources.cs` for discoverability.
5. **Add a `CHANGELOG.md`** entry (top, an `## Unreleased — <title> (<old> -> <new> tools)` section).
6. **Add a test** in `src/WkMcp.Tests/` — at minimum the input-validation/early-return branch
   (pure file-op tools can be tested end-to-end with a temp dir; daemon-backed ops just test the gates).
7. **Build + test** (with the OutDir trick if the server is live).

### Tool-count discipline (the recurring footgun)

The total is reflection-derived, but several **hand-written** numbers must match. When the count
changes, grep the **old** number and update every hit:

- `README.md` — `**N tools**`, `(N offline + 36 live_*)`, the directory-tree comment, the docs link.
- `docs/TOOLS.md` — the `> **Counts.**` header (`N offline tools` … `N tools in total`).
- `docs/ARCHITECTURE.md` — `**N tools** (X base + 51 workflow/inspection + 1 probe + 36 live)`.
- `docs/HTTP_TRANSPORT.md` — `the N tools`.
- `manifest.json` — `long_description` (`N tools: …`).
- `src/WkMcp/Program.cs` — the `// The N tools …` comment.
- `WolvenKitResources.cs` — the total is **computed** (`offline+modding+scene+probe+live`); the
  "Full list" blocks are reflection-based and auto-update, but the "Key tools by task" lines are hand-written.

"offline" = all non-`live_*` tools (currently 132); "live" = 36; total = 168.
A base-category tool lives in `WolvenKitTools.cs`; the ARCHITECTURE breakdown's "X base" tracks that class.

## Daemon — adding a verb

The daemon dispatches `verb + argv`. Two kinds:

- **`ConsoleFunctions` verbs** — WolvenKit's own CLI task wrappers (`hash`, `archive`, `unbundle`,
  `uncook`, `export`, `import`, `pack`, `build`, `convert`, `conflicts`, `oodle`, `wwise`). Add a
  `case` in the main `switch` calling `f.SomeTask(...)`.
- **Special verbs** — when you need a Modkit call `ConsoleFunctions` doesn't expose (e.g. `loc-resolve`,
  `opus-import`, `export-entity`, `import --garment`). Pattern: `provider.CreateScope()` →
  `sp.GetRequiredService<IModTools>()` / `IArchiveManager` / `Red4ParserService` → read the CR2W →
  call the method → `logger.Info/Success/Error`. Copy an existing special verb as a template.

The typed `import_*` tools are **thin ergonomic wrappers** over the single daemon `import` verb
(`+ --keep`); `ModTools.ImportGltf` dispatches mesh/rig/anim/morphtarget by the **type embedded in
the exported file name** (`foo.rig.glb` → `TypeFromFileExt`). Garment support is **off by default**
(`GltfImportArgs.ImportGarmentSupport`) — `import --garment` turns it on.

### Discovering the WolvenKit API (reflection / decompilation)

There is no vendored WolvenKit source. To learn a real signature or behavior, reflect/decompile the
NuGet DLLs (resolve deps from `src/WkDaemon/bin/Release/net8.0`):

- **Signatures**: a throwaway .NET 8 console using `System.Reflection.MetadataLoadContext` +
  `System.Reflection.PathAssemblyResolver` pointed at the daemon's `bin`. (PowerShell 5.1 / .NET
  Framework lacks `MetadataLoadContext` — use a `dotnet run` helper.)
- **Behavior** (how a method dispatches): decompile with the `ICSharpCode.Decompiler` NuGet
  (`CSharpDecompiler.DecompileAsString(method.MetadataToken)`).

Before adding a tool that mirrors a Modkit capability, **verify it's a real, non-redundant capability** —
several plausible "gaps" turn out to be already covered (e.g. the generic `import` on a folder already
calls `ModTools.ImportFolder`; `extract_audio` already does opus bulk + by-hash) or not to exist at all
(no Wwise *import* in Modkit). Record drops with their rationale in the CHANGELOG.

## Conventions & gotchas

- **Structured JSON only** — every tool returns the `{ ok, status, summary, produced, warnings, errors, log }`
  envelope; agents parse it. Don't return free text.
- **Path safety** — install/write tools validate the game folder and refuse paths outside the intended
  subtree (see `PathSafety.cs`, `install_*`, `uninstall_*`). Keep that.
- **Culture** — the host may run `fr-FR`; format engine values (floats, TweakDB flats) with
  `InvariantCulture`, or you get `,` decimals that break the game/TweakXL.
- **`game_probe` / cores** — `mod_doctor` and `diagnose_logs` have extracted `*Core` methods reused by
  `game_probe`; keep their JSON output identical if you touch them.
- **Real-asset validation** — the unit suite does not round-trip real `.mesh`/`.archive` (no game in CI).
  Asset-touching changes (import/export/uncook) need a manual check against a real install; flag that
  rather than claiming it's verified.
- **Security** — never harvest/print git credentials or tokens. The game install and any live-game
  exploration require the user's explicit go-ahead.

## Git workflow

- Branch off `main` (`feature/<topic>`); don't commit to `main` directly.
- Commit **only when asked**; **push / open a PR only when explicitly asked** (separate steps).
- Use `--force-with-lease` for any force-push.
- End commit messages with the project's Co-Authored-By trailer; end PR bodies with the
  Claude Code "Generated with" line.
- CI (`ci.yml`) runs on PRs: build daemon → build server → test → build & validate the `.mcpb`. Keep it green.

## These instruction files

`AGENT.md` is the source of truth; `CLAUDE.md` imports it. Update `AGENT.md` (not both) when the
build/test/convention facts change, and keep the tool counts here in sync via the discipline above.
