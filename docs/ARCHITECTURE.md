# Architecture — WkMCP

Document intended for a **future contributor**. It explains *why* the project
is structured the way it is, *how* the pieces communicate, and *how* to add
code to it without breaking the invariants. Paths are given relative to the
repository root (`wkmcp/`).

---

## 1. Overview

The project is an **MCP server** (Model Context Protocol) that exposes
WolvenKit's Cyberpunk 2077 modding capabilities to an LLM agent. It consists of
**two processes**:

```
Claude ─MCP/JSON-RPC (stdio)─▶ WkMcp ─IPC stdio JSON─▶ WkDaemon ─▶ WolvenKit libs + libkraken
                                            └─fallback───────▶ cp77tools (subprocess) if daemon unavailable
```

- **`src/WkMcp`** — the MCP host. It speaks MCP JSON-RPC to the client
  (Claude) over stdio (or opt-in HTTP/Streamable), exposes **123 tools** (63 base +
  25 workflow + 35 live), **8 prompts** and **4 resources**, and links *no*
  WolvenKit library. It drives the daemon over IPC.
- **`src/WkDaemon`** — a persistent process that links the WolvenKit
  libraries (`WolvenKit.Modkit`, `WolvenKit.RED4.CR2W`, …) and `libkraken`/Oodle.
  It loads the heavy reference data **once** then processes requests in a loop.
- **`src/WkMcp.Tests`** — the xUnit test suite (pure helpers: `Truncate`,
  `MatchesGlob`, `BuildCpmodprojXml`, REDscript lint, archive histogram,
  REDmod validation, `.app` summary, doc safeguard, etc.).

---

## 2. Why two processes?

Two reasons, in this order of importance.

### 2.1 License isolation (GPL-3.0)

WolvenKit's libraries are under **GPL-3.0** (copyleft). Linking these
assemblies *in the same process/assembly* as the MCP server would contaminate the
latter with the copyleft. By isolating all GPL code in a **separate process**
(the daemon) that the MCP server only talks to via **stdio IPC** (exchanging
JSON text, no assembly link), the MCP server stays outside the scope of the
copyleft. This is the project's central separation boundary: **`WkMcp`
never references a WolvenKit assembly; only `WkDaemon` does.**

### 2.2 Performance: pay the cold-start only once

WolvenKit's dominant cost is loading `HashService` (the hash ↔ REDengine path
database): **~6 s**. The `cp77tools` CLI pays this cost *on every
invocation*. By keeping the daemon alive, this cost is paid **once** at
startup; subsequent requests only cost a few milliseconds of IPC +
the real work.

See `src/WkDaemon/Program.cs`: `HashService`, `TweakDBService`,
`LocKeyService`, `HookService` are **singletons** ("hot"), and an
explicit warmup builds `ConsoleFunctions` at startup before signaling
`{"ready":true}`.

---

## 3. The pipelined stdio JSON IPC protocol

### 3.1 Message format (one JSON line per message)

Reference: header of `src/WkDaemon/Program.cs`.

```
request  : {"id":N,"argv":["unbundle","/x.archive","--outpath","/out"]}
response : {"id":N,"exit":0,"output":"[ 0: Information ] - ..."}
on ready : {"ready":true}
```

- `argv` is exactly the `cp77tools`-style command line (verb +
  arguments). The daemon dispatches the verb to a WolvenKit method.
- `output` aggregates everything the command wrote (logger + captured
  `Console.Out`, see §3.4).
- The DTOs are `DaemonRequest(int id, string[] argv)` and
  `DaemonResponse(int id, int exit, string output)`.

**The daemon's `stdout` is strictly reserved for the JSON protocol.** A reference
`channel = Console.Out` is captured *before* any redirection, then
`Console.Out` is reassigned to a capturing writer (see §3.4). All
responses are written via `channel`. On the MCP server side, same discipline: `stdout`
is reserved for MCP JSON-RPC, so **all logs go to stderr**
(`Program.cs` configures `LogToStandardErrorThreshold = Trace`).

### 3.2 Pipelining and correlation by `id`

The transport is **pipelined**: several requests can be in flight
simultaneously, re-paired by `id`. On the MCP server side
(`src/WkMcp/Cp77ToolsRunner.cs`):

- `SendToDaemonAsync` assigns an incremental `id`, registers a
  `TaskCompletionSource` in the concurrent dictionary `_outstanding[id]`, then
  writes the JSON line to the daemon's stdin. The stdin writes are serialized
  by `_writeLock` (a single writer at a time, otherwise two JSON lines
  would interleave).
- `ReadResponseLoopAsync` is a single read loop that runs for
  the entire life of the daemon: for each line received, it parses the `id` and completes
  the corresponding `TaskCompletionSource`. When the daemon dies (stream closed),
  it **fails all in-flight requests** so as not to leave pending calls.

### 3.3 Serialization on the execution side

Although the IPC is pipelined, **execution in the daemon stays serialized** by
an `execLock` (SemaphoreSlim 1,1): the WolvenKit libraries (logger, archive
manager, captured `Console.Out`) **are not thread-safe**. The useful overlap:
decode/receive request N+1 while N executes, and let the client
send in a pipeline without waiting for the previous response. The response
writes are also protected by a `writeLock`.

### 3.4 Output capture

Two WolvenKit output channels must be aggregated:

1. `ILoggerService` — most messages. The daemon provides
   `CapturingLoggerService`, which accumulates in a drainable `StringBuilder` in
   the `[ 0: Level ] - message` format (identical to `cp77tools`).
2. `Console.WriteLine` — some tasks (notably the `archive --list`
   listing) write their result directly to the console. Hence the
   `CapturingTextWriter` that redirects `Console.Out` to the **same** buffer.
   Without it, `archive_info` / `find_in_archives` / the archive resource
   would return empty.

On each request, the daemon `Drain()`s the buffer before execution, executes, then
`Drain()`s again to retrieve exactly that request's output.

---

## 4. The runner: warmup, LRU cache, metrics, fallback

Everything lives in `src/WkMcp/Cp77ToolsRunner.cs`. A **single shared
instance** (`Cp77ToolsRunner.Shared`) is injected via DI into all tools and
reused by the resources — so **a single daemon** for the whole server.

### 4.1 Warmup

`Program.cs` launches, at startup and in the background, a
`--version` call:

```csharp
_ = Task.Run(() => Cp77ToolsRunner.Shared.RunAsync(new[] { "--version" }, CancellationToken.None));
```

This starts the daemon and triggers its warmup (~6-8 s) while the
client connects, so that **even the very first real tool call** benefits
from an already-hot `HashService`.

`EnsureDaemonAsync` manages the lifecycle: lock-free fast-path if the daemon is
alive, otherwise startup under `_initLock` (double-check), waiting for
`{"ready":true}` with a 90 s timeout, continuous draining of stderr (otherwise the
pipe buffer would block the daemon), then launching the read loop.

### 4.2 LRU cache of archive listings

`GetArchiveListingAsync` caches the list of internal paths of an
`.archive` (`_archiveCache`, key = absolute path). Invalidation is based on the
file's **mtime** (`LastWriteTimeUtc`): if the mtime has changed, we rerun
`archive --list` and replace the entry. The counters `_cacheHits` /
`_cacheMisses` are exposed via the `wk_status` tool.
`InvalidateArchiveCache` allows purging (the `clear_cache` tool).

### 4.3 Per-verb metrics

`RunAsync` times each call and records it in `_metrics` (one
`RunnerMetrics` per verb). `RunnerMetrics` keeps `Count`, `TotalMs`, and a
**circular ring of the last 100 durations** from which **p50 / p95** are
computed. Exposed via `wk_status` (key `metrics`), resettable via
`clear_cache(scope=metrics|all)` → `ResetMetrics()`.

### 4.4 cp77tools fallback

If the daemon fails (exception other than cancellation), `RunAsync` kills it
(`KillDaemon`) — it will be restarted on the next call — then falls back to
`RunViaSubprocessAsync`, which reruns the `cp77tools` CLI as a subprocess
(original behavior, ~6 s/call, but functional). If the daemon DLL is
flat-out **missing**, the fallback becomes permanent (`_daemonDisabled = true`).

Path resolution (all by environment variables, otherwise defaults):
`WKMCP_DAEMON` (daemon DLL, otherwise sibling project), `WKMCP_CP77TOOLS`
(cp77tools exe, otherwise `~/.dotnet/tools` then PATH), `DOTNET_ROOT` /
`WKMCP_DOTNET_ROOT`, `WKMCP_CLI_TIMEOUT_SECONDS` (default 300 s).

---

## 5. Tool result convention

Each tool returns a JSON:

```json
{ "ok", "status", "summary", "produced", "warnings", "errors", "exitCode", "log" }
```

- `status` ∈ `success | partial | error | timeout`.
- **Success is determined by the files actually produced, not by a
  log marker.** The `Structured` helper distinguishes two cases:
  - **producer tool** (it is passed a `produced` list): `success` if
    files appeared (and `partial` if there are also errors), `error`
    otherwise. This prevents a non-fatal error (e.g. exporting a mesh's materials)
    from failing a call that did indeed produce the expected file.
  - **information tool** (no `produced`): we rely on the exit code and the
    absence of "Daemon error" / "Unhandled" markers.
- `warnings` / `errors` are extracted from the log by `LogLines` (lines
  `[ 0: Warning/Error ] - …`).
- `log` is truncated by `Truncate` (12,000 chars) while preserving head, mid-log
  errors, and tail.

Utility helpers in `WolvenKitTools.cs`: `Err` (argument validation
failure), `Snapshot`/`ProducedIn`/`WithSnapshot` (directory diff to
compute `produced`), `MatchesGlob`, `BuildCpmodprojXml`.

---

## 6. Guide: adding a new MCP tool

A tool is a **static method** in a `[McpServerToolType]` class
(`WolvenKitTools.cs` for the 63 base tools, `ModdingTools.cs` for the 25
high-level workflow tools, `LiveTools.cs` for the 35 live tools).
**Registration is automatic via reflection**: `Program.cs` calls
`.WithToolsFromAssembly()`, so there is nothing to register manually.
Declare the hints on the attribute (`ReadOnly`/`Destructive`/`Idempotent`) — a
test (`ConsistencyTests`) fails if they are missing.

### 6.1 Skeleton

```csharp
[McpServerTool(Name = "my_tool")]
[Description("Clear sentence for the LLM: what the tool does, its limits, " +
             "and the expected output format.")]
public static async Task<string> MyTool(
    Cp77ToolsRunner runner,   // injected by DI (shared instance → single daemon)
    [Description("Describe each parameter — the LLM uses it to call the tool.")] string input,
    [Description("Output path.")] string output,
    CancellationToken ct = default)
{
    if (!File.Exists(input))
        return Err($"File not found: {input}");          // validation failure

    var r = await runner.RunAsync(new[] { "verb", input, "--outpath", output }, ct);

    return Structured($"My processing: {input} → {output}", r,
        File.Exists(output) ? new List<string> { output } : new List<string>());
}
```

### 6.2 Rules to follow

- **Signature**: `[McpServerTool(Name = "...")]` + `[Description]` on the method
  and on each parameter. First parameter `Cp77ToolsRunner runner` if the tool
  delegates to the daemon; `CancellationToken ct = default` last.
- **Delegation**: call `runner.RunAsync(argv, ct)` with the `argv` in
  cp77tools style. If the verb does not yet exist on the daemon side, add it (see §7).
- **Result**: always return JSON via `Structured(...)` (or
  `Err(...)`/an anonymous object conforming to the §5 schema). For a producer tool,
  pass the `produced` list computed from the files actually written
  (use `WithSnapshot` when the output is a directory whose content you don't
  know in advance).
- **Upfront validation**: check the existence of inputs and create the
  output directories *before* calling the daemon; return `Err` early.
- **Write nothing to stdout** directly (reserved for JSON-RPC).
- Add a test in `WkMcp.Tests` if the tool contains pure
  logic (parsing, formatting, path construction).

---

## 7. Guide: adding a daemon verb

Everything happens in the `Dispatch(...)` dispatcher of
`src/WkDaemon/Program.cs`, which maps `argv[0]` (the verb) to a method.

### 7.1 Two families of services

| Type | Lifecycle | Why |
|------|--------------|----------|
| **Hot singletons** | `HashService`, `TweakDBService`, `LocKeyService`, `HookService` | Expensive to load (~6 s); shared and kept hot. |
| **Scoped (per request)** | `ArchiveManager`, `ModTools`, `Red4ParserService`, `MeshTools`, `ConsoleFunctions` | Accumulate state (e.g. `ArchiveManager` remembers loaded archives); **must be fresh on each request** otherwise state leaks between calls. |

Practical consequence: for a verb that loads game archives, **always create
a dedicated scope**:

```csharp
using var scope = provider.CreateScope();
var am = scope.ServiceProvider.GetRequiredService<IArchiveManager>();
am.LoadGameArchives(exe);
// ... work ...
```

For a verb that only needs hot reference data (e.g.
`resolve-hash`, `tweakdb-resolve`), read the singleton directly from the root
`provider`, without a scope.

### 7.2 ConsoleFunctions

Most "standard" verbs (`unbundle`, `uncook`, `export`, `import`,
`pack`, `build`, `convert`, `archive`, `oodle`, `wwise`, `hash`) delegate to
methods of **`ConsoleFunctions`** (from `WolvenKit.Modkit` / `CP77Tools`),
resolved via a scope at the end of `Dispatch`. This is the real implementation of the
WolvenKit tasks. The "home-grown" verbs (`loc-resolve`, `opus-import`,
`tweakdb-*`, `tweak read|write|validate`) are implemented upstream of the switch,
directly with the services.

### 7.3 ParseArgs

`ParseArgs(argv, skip)` splits the `argv` into **positionals** and **options**
`--key value`. Boolean flags (without value) are declared in the `HashSet`
`boolFlags` (`--list`, `--diff`, `--keep`, `--serialize`, …) — **if you add a
new boolean option, add it to this set**, otherwise the parser will consume
the next argument as its value. Associated helpers: `Opt` (option value),
`Dir` (→ `DirectoryInfo`), `Fs` (file or folder), `ParseUext`,
`ParseUintList`.

### 7.4 Skeleton of a new verb

```csharp
if (verb == "my-verb")
{
    var (pos, o) = ParseArgs(argv, 1);
    if (pos.Count == 0) { logger.Error("my-verb: missing argument"); return -1; }

    using var scope = provider.CreateScope();
    var am = scope.ServiceProvider.GetRequiredService<IArchiveManager>();
    // ... work, write via logger.Info/Warning/Error ...
    logger.Info("my-verb: done");
    return 0;   // 0 = success, ≠0 = failure (the MCP server maps it to status)
}
```

The **return code** and the **output** (via `logger`) are what the MCP server
will receive in `{exit, output}`. For large volumes (e.g. TweakDB dumps), apply
a **hard cap on the daemon side** (see `tweakdb-query`, cap 100 + `+` marker to
signal truncation) so as not to saturate the LLM's context.

### 7.5 Pitfall: content assemblies

Some dependencies (`DirectXTexNet`, etc.) are shipped as content files,
copied into the build output but **absent from `deps.json`**. The
daemon installs a fallback resolver
(`AssemblyLoadContext.Default.Resolving`) that loads them from the
application folder. And `Oodle.Load()` is called at startup to load the native
Kraken codec. Do not remove these two initializations.

---

## 8. The REDscript parser and its anti-false-positive philosophy

`src/WkMcp/RedscriptParser.cs` powers the `lint_script` tool. It is a
home-grown **tokenizer + recursive-descent parser** that validates the **syntax**
of an isolated `.reds` file. **It is NOT a type-checker**: it does not resolve
external types/methods (that would require the `scc` compiler and the entire
mod ecosystem, see `WINDOWS-VALIDATION.md`).

### 8.1 Philosophy

Calibrated for **0 false positives** on a corpus of **1374 real `.reds` files**. The design
rule:

- **Strict** on the stable structure: declarations (`class`/`struct`/`enum`/
  `func`/`let`), types, statement headers, balancing of `() [] {}`.
- **Permissive but balanced** on expressions: we do **not** model operator
  precedence. An expression is consumed up to its terminator
  (`;` or `}`) while checking only the balancing of parentheses/brackets/
  braces (`SkipExpression`, `SkipExpressionUntil`, `SkipBalanced`). Imposing a
  complete expression grammar would risk rejecting valid REDscript — hence
  this deliberately tolerant choice.

### 8.2 Notable mechanisms

- **Lexer**: handles `//` and `/* */` comments (flags unclosed ones),
  prefixed strings (`n"…"`, `s"…"`) and **interpolation** `\( … )` with
  nested quotes and multi-line strings, numbers with suffixes (`u`, `f`,
  `ul`…), `@…` annotations, compound operators.
- **Targeted tolerance**: `if`/`while`/`for`/`switch` without mandatory parentheses
  (`SkipExprUntilBlock`), `-> { }` lambdas, shorthand types `[T]` / `[T; N]`,
  generics `<T, U>` (`SkipAngles`, character-by-character counting to handle
  `>>`), `native func` without body (signature followed by a declaration
  boundary), expression-body functions `= expr` (`SkipExprBody`),
  contextual keywords used as names.
- **Error recovery**: `Synchronize()` resynchronizes on `;`, `}`, or
  a declaration keyword, to avoid **cascades** of false diagnostics.
  Safeguard `MaxErrors = 60` and a progress guarantee (`if (_p == before)
  Next();`) to never loop.

---

## 9. Build, test, validation

### 9.1 Build order (the daemon first)

```sh
dotnet build src/WkDaemon   # also deploys the natives (libkraken/kraken.dll, DirectXTexNet)
dotnet build src/WkMcp
```

The daemon must be built **before** the MCP server, because the server resolves by
default the daemon DLL in the sibling project (`WKMCP_DAEMON` allows
overriding it).

### 9.2 Tests

```sh
dotnet test                        # the xUnit suite (src/WkMcp.Tests)
python3 test-daemon.py             # daemon-only latency, per request
python3 test-mcp-server.py         # end-to-end MCP server
python  test-new-tools.py "<game>" # real-game validation: archive_stats / validate_redmod / inspect_app
```

### 9.3 End-to-end validation on real assets (Windows)

```sh
python3 validate-windows.py        # exercises the tools + prompts on real game assets
```

See `dev/WINDOWS-VALIDATION.md` for the details and prerequisites (game installation,
TweakDB, etc.).

### 9.4 DLL lock pitfall (Windows) — kill the processes before rebuild

As long as a `WkDaemon` (or an MCP server holding it open) is running, its
DLLs are **locked** by Windows and `dotnet build` fails with a
file-in-use error. **Before recompiling, kill the processes**:

```powershell
Get-Process dotnet, WkDaemon, WkMcp -ErrorAction SilentlyContinue | Stop-Process -Force
```

(Adapt the names according to the launch mode.) Also remember to close the
MCP client — Claude Desktop restarts the server, which keeps the daemon alive.

---

## 10. Map of key files

| File | Role |
|---------|------|
| `src/WkMcp/Program.cs` | MCP host, stdio transport, DI, daemon warmup. |
| `src/WkMcp/Cp77ToolsRunner.cs` | Daemon driver: pipelined IPC, LRU cache, p50/p95 metrics, cp77tools fallback. |
| `src/WkMcp/WolvenKitTools.cs` | 63 base MCP tools + helpers (`Structured`, `Err`, `Truncate`, `MatchesGlob`, `Snapshot`/`ProducedIn`, `BuildCpmodprojXml`). |
| `src/WkMcp/ModdingTools.cs` | 25 high-level workflow tools + framework knowledge base. |
| `src/WkMcp/LiveTools.cs` | 35 `live_*` tools (running game, via CetBridge). |
| `src/WkMcp/CetBridge.cs` | TCP/file bridge to the CETBridge Lua mod (live game). |
| `src/WkMcp/RedscriptParser.cs` | REDscript tokenizer + recursive parser (`lint_script`). |
| `src/WkMcp/WolvenKitResources.cs` | MCP resources (`[McpServerResourceType]`). |
| `src/WkMcp/WolvenKitPrompts.cs` | MCP prompts (`[McpServerPromptType]`). |
| `src/WkDaemon/Program.cs` | DI, verb dispatcher, IPC, shims (`CapturingLoggerService`, etc.). |
| `src/WkMcp.Tests/` | xUnit tests of the pure helpers. |
