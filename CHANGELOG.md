# Changelog — WkMCP

Dates are those of the development sessions.

## Unreleased — clone_tweak_record (faithful TweakDB record clone)

New daemon verb `tweakdb-clone` + MCP tool **`clone_tweak_record`** (**151 → 152 tools**,
116 offline). Closes the gap left in the previous entry: a *faithful* clone of an existing
TweakDB record, not the blind skeleton `generate_tweak_template` produces.

- Verifies `baseId` exists in the `tweakdb.bin`, then emits `<newId>:` with **`$base: <baseId>`**
  — TweakXL's documented clone attribute copies every property of the base record at load, so
  correctness does not depend on us serializing each flat.
- Appends a **commented inventory of all the base's flats with their current values**, rendered
  properly (TweakDBIDs resolved to ids, floats in `InvariantCulture` — the engine values use `.`
  and a fr-FR host's default `ToString()` would emit `,`, arrays expanded, `Vector3` as
  `{ x, y, z }`). The author uncomments only what they want to override; unhandled types
  (LocKey wrapper, resource refs) stay as a `<TypeName>` tag in the comment, harmless.
- Optional `overridesJson` `{field: value}` is emitted as active keys (removed from the inventory).
- Validated end-to-end against the real `tweakdb.bin` (`Items.Preset_Lexington_Default`,
  gamedataWeaponItem_Record, 140 flats): faithful `$base`, all values rendered, unknown base →
  hard error with no file written, overrides path correct.

Note discovered while building this: TweakXL has **no `$instanceOf` attribute** (its docs define
`$base` for cloning and `$type` for a new record's type). `generate_tweak_template`'s
`new_record`/`new_item` emit `$instanceOf` — likely ineffective; flagged for a separate fix.

## Unreleased — Gameplay-logic inspectors (quest phases / communities)

Continues the tooling-gap work onto the two file families that drive quest flow and world
population and still had no dedicated tooling — only the generic `inspect_cr2w`. New
`GameplayInspectionTools.cs` (a third partial of `ModdingTools`) adds **2 tools**; each accepts
a binary CR2W (converted via the daemon) or its `.json`. The pure cores were validated against
real Cyberpunk 2077 files (extracted + converted): `teddy_holocall` / `sq023_bd_studio`
questphases, `wbr_hil_rippdoc` / `sq017_caliente` / `q003_militech` communities.
**149 → 151 tools** (113 → 115 offline). +9 tests (218 → 227), of which 2 are env-guarded
real-file smoke tests (`WKMCP_TEST_QUESTPHASE` / `WKMCP_TEST_COMMUNITY`).

- **`inspect_questphase`** — a `.questphase` (questQuestPhaseResource): nodes with a per-type
  histogram, the **node→node edges reconstructed from the CR2W socket-handle graph** (each
  connection references its sockets by `HandleId`/`HandleRefId`; sockets are mapped to their
  owning node), entry/exit nodes (`questInput`/`questOutput`), and the `.scene` files and
  external sub-phases it triggers.
- **`inspect_community`** — a `.community` (communityCommunityTemplate): each spawn entry with
  its `Character.*` record, appearances, spawn phases and per-phase time periods (Day/Night
  quantities), plus voice-tag initializers; rolls up distinct characters.

(`clone_tweak_record` was deferred at this point, then implemented in the entry above.)

## Unreleased — Asset-inspection coverage (materials / UI / rig / diff / Nexus)

Fills the tool-surface gaps for asset families that had no dedicated tooling. Gap analysis
was cross-checked with two local models (Kimi-K2.7, GLM-5.2 via Ollama) and verified against
the code. New `AssetInspectionTools.cs` (a second partial of `ModdingTools`) adds **10 tools**;
each accepts a binary CR2W (converted via the daemon) or its `.json`. The pure analysis cores
are unit-tested against synthetic CR2W-JSON fixtures (no live game install required).
**139 → 149 tools** (103 → 113 offline). +14 tests (204 → 218).

### Materials
- **`inspect_material`** — a `.mi` (CMaterialInstance): `baseMaterial` + every parameter with
  its kind (color/scalar/texture/vector) and value; textures expose their DepotPath. Handles
  both the "single-key wrapper" and `{Key,Value}` value shapes.
- **`inspect_mlsetup`** — a `.mlsetup` (Multilayer_Setup): per-layer material/microblend refs +
  `colorScale`, `opacity`, `normalStrength`, tiling.
- **`edit_material_instance`** — sets ONE named parameter (texture/color/scalar/string) of a
  `.mi` and writes edited JSON (→ `json_to_cr2w` / `write_game_file`). Pure JSON in→out.
- **`trace_material_chain`** — follows resource refs from a `.mesh`/`.app`/`.ent`/`.mi`/`.mlsetup`
  down to the textures it uses, resolving across a `depotRoot` folder and/or the base game.

### UI / rig
- **`inspect_inkatlas`** / **`resolve_inkatlas_part`** — `.inkatlas` sprites: textures + named
  parts with UV/pixel clipping rects; targeted lookup of one part by name.
- **`inspect_inkwidget`** — `.inkwidget` library: named items + widget-type histogram.
- **`inspect_rig`** — `.rig` (animRig): bone count, roots, depth, each bone → parent (cycle-guarded).

### Diff / packaging
- **`diff_cr2w`** — generic field-level diff of any two CR2W files (or their JSON), with JSON
  paths; generalizes `diff_mod_vs_base`.
- **`package_for_nexus`** — Nexus pre-flight (auto-quarantine binary guard, layout check,
  dependency report) then a `/`-separated `.zip`; `allowBinaries` for RED4ext/CET mods.

## Unreleased — Scene (.scene / scnSceneResource) support

First-class support for `.scene` files (the quest/dialogue scene system), which had no
dedicated tooling before — only the generic `inspect_cr2w` / `find_in_cr2w`. New
`SceneTools.cs` adds **11 scene-aware tools** that work offline via the daemon's
`convert serialize/deserialize` (each accepts a `.scene` or its converted `.json`).
**128 → 139 tools** (92 → 103 offline), **8 → 10 prompts**.

### Inspect / graph / validate / translate
- **`inspect_scene`** — structural summary: node count + histogram by node type, actors,
  screenplay lines/options, entry/exit/notable points, version.
- **`scene_graph`** — the narrative flow: nodes + output→input socket edges; choice nodes
  carry their option captions. Bounded with a `truncated` flag.
- **`find_in_scene`** — locate a node or dialogue line by id, node `$type`, or resolved
  text; returns JSON paths for `read_game_file` / `write_game_file`.
- **`validate_scene`** — graph integrity (unique ids; start/end present + resolve; every
  output-socket destination resolves; reachability), actor refs, dialogue refs
  (dialogLineEvent → screenplay line; choice option → screenplay option; locstrings resolve
  to non-empty embedded text), and choice-option/socket consistency. Correctly **whitelists
  the `scnCutControlNode` backup socket** (`stamp.name=1026`) and **ignores
  `scnDeletionMarkerNode` tombstones** (only warns on a live edge into one).
- **`extract_scene_localization`** / **`apply_scene_localization`** — dump a scene's
  dialogue (resolving the embedded loc store, with speaker) to a translations JSON, then
  write edited text back into the loc store and re-serialize. The write-back does a
  **control round-trip** and warns if a string did not survive (WolvenKit can mis-serialize
  some scenes). Scenes whose text is localized externally yield text=null and are not written.

### Dependencies / events / editing / scaffold
- **`scene_dependencies`** — lists every EXTERNAL reference a scene needs (animation `.anims`
  from `resouresReferences`, `ridResources`, prop `.ent`, plus actor TweakDB character records),
  which the internal validator never checked. Optionally resolves against a mod folder
  (`modRoot`): paths the mod ships are `inMod`, base-game-prefixed paths are assumed present, the
  rest are flagged unresolved — catching a custom asset the mod forgot to include.
- **`scene_events`** — per-section timeline of what plays (dialogue with resolved text + speaker,
  animation name, audio, camera/VFX) with startTime/duration. Complements `inspect_scene`.
- **`scene_set_actor`** — retarget an actor's `specCharacterRecordId` / `specAppearance` by id.
- **`scene_replace_resource`** — swap a depot path everywhere it appears (e.g. a `.anims`).
  Both re-serialize with the control round-trip of `apply_scene_localization`.
- **`scaffold_scene`** — generate a minimal valid `scnSceneResource` skeleton (start → N sections
  → end, auto node ids + wired sockets) that passes `validate_scene` and converts to a `.scene`
  via `json_to_cr2w` (verified end-to-end against the CLI). A skeleton generator, not a full
  authoring suite.
- New prompts `translate_scene` and `audit_scene`; docs [docs/SCENES.md](docs/SCENES.md).
  Verified end-to-end against a real game scene (`WKMCP_TEST_SCENE` smoke test) plus
  synthetic-JSON unit tests.

## 2.0.0 — Renamed to WkMCP + audit hardening

A multi-axis audit (security, daemon performance, live bridge, code quality,
features) drove the following. **123 → 128 tools** (92 offline + 36 live).

### Rebrand (breaking) — WolvenKit MCP → WkMCP
With the WolvenKit team's blessing, the project is renamed **WkMCP** (it is no longer
*named* after WolvenKit, only descriptive references remain — it still wraps the WolvenKit CLI).
- Namespace/assembly/projects `WolvenKitMcp`→`WkMcp`, `WolvenKitDaemon`→`WkDaemon`;
  DLLs `WkMcp.dll` / `WkDaemon.dll`; bundle `wkmcp.mcpb`; manifest slug `wkmcp`.
- Env vars `WOLVENKIT_*` → `WKMCP_*` (DAEMON, CP77TOOLS, CLI_TIMEOUT_SECONDS, DOTNET_ROOT,
  TRANSPORT, HTTP_URL, HTTP_TOKEN). **The old names still work** as a fallback so existing
  configs don't break silently.
- Tool `wolvenkit_status` → **`wk_status`**. Resource URI scheme `wolvenkit://` → `wkmcp://`.
- Repository moved to `github.com/Guikingone/WkMCP`.

### Security & correctness
- **Path traversal fixed** in `write_game_file` and `toggle_mods` via a new, unit-tested
  `PathSafety` helper (rejects rooted / `..` paths). DNS-rebinding guard on the HTTP
  transport (always-on `Host`/`Origin` validation, even in tokenless loopback mode).
- `read_game_file` no longer returns a stale prior extraction (temp folder wiped per call).
- `live_set_stat` now SETS instead of STACKING additive modifiers (was non-idempotent and
  could never lower a stat). `live_spawn_vehicle` no longer advertises a dead `distance`
  param. Misleading annotations fixed (`launch_game` destructive; `restore_mods` non-idempotent).
- Cancelled `wwise_export`/`find_in_archives`/`lint_mod` return structured JSON, not a raw
  exception. Invariant-culture YAML numbers (fr-FR emitted `1,5`). Unified error JSON shape.

### Performance
- Read-only `resolve_hash`/`tweakdb_resolve` routed around the daemon's global exec lock
  (responsive even during a long uncook). Cached game `ArchiveManager` (exe+mtime).
  Archive-listing single-flight + bounded LRU. Daemon killed on process exit; absolute
  wall-clock backstop on the inactivity timeout. Buffered console capture.

### Features
- **`find_record_by_name`** — reverse displayName → TweakDBID lookup.
- **`diff_against_installed`** — working-build vs installed-copy file-set diff.
- **`live_unobserve`** — cancels a `live_observe` subscription (fixes an observer leak).
- **`set_texture_format`** — sets the group/compression/rawFormat of an extracted `.xbm`
  (the #1 silent retexture failure); CR2W round-trip via JSON.
- **`add_appearance`** — adds an appearance to a `.app` by cloning an existing one, with
  fresh-renumbered CR2W `HandleId`s (no aliasing), optional mesh-path swaps, and a
  round-trip self-verification that the new appearance survives deserialization.
- **`generate_tweak_template` → `new_item`** — typed item scaffolds
  (weapon/clothing/cyberware/consumable/recipe).

### CI / supply chain
- Third-party actions pinned to commit SHAs; `ci.yml` least-privilege permissions;
  `.mcpb.sha256` checksum attached to releases.

## 1.0.1 — 2026-06-19

Hotfix: the `.mcpb` bundle failed to install / preview (Claude Desktop and Nexus)
because `manifest.json` carried keys outside the MCPB manifest schema.

### Fixes
- Removed the non-schema top-level `bugs` key (replaced by the spec's `support` URL).
- Removed `compatibility.runtimes.dotnet` — `compatibility.runtimes` only accepts
  `python`/`node`; the .NET 8 requirement stays documented in the description/README.
  `compatibility` is now just `{ "platforms": ["win32"] }`.

No tool/behavior change; rebuild of the bundle only.

## 1.0.0 — 2026-06-17

First public, stable release. The server is functionally complete (123 tools,
8 prompts, 4 resources) and the packaging and documentation are now v1-ready.

### Documentation
- README rewritten as a v1 landing page: a Windows quickstart (~5 min), highlights,
  a one-click `.mcpb` install path, a tools/prompts/resources overview (the full tool
  table moved to `docs/TOOLS.md`), the license split, troubleshooting, and a corrected
  repository tree. The "Prototype" framing and the macOS-first install were removed.
- `docs/` de-duplicated and expanded: `TOOLS.md` gained a table of contents and full
  parameter tables for §20; `LIVE_BRIDGE.md` now documents all 35 `live_*` tool
  parameters; `HTTP_TRANSPORT.md` adds a "why HTTP" intro, a complete curl example and
  troubleshooting; `MODDING_RECIPES.md` opens with a "which recipe" decision table;
  `USER_GUIDE.md` troubleshooting expanded; `ARCHITECTURE.md` count fixes.
- `native/README.md` translated to English; French comments in `.gitignore` and the
  `.csproj` files translated.

### Packaging & metadata
- `manifest.json`: version `1.0.0`, plus `author`, `repository`, `homepage`, `bugs`
  and discoverability keywords.
- License reconciliation: root `LICENCE` (MIT) for the server source;
  `src/WkDaemon/LICENSE` (GPL-3.0 — the daemon links WolvenKit's GPL libs);
  a root `NOTICE` explaining the per-component split; the `.mcpb` manifest license
  stays GPL-3.0 (the bundle ships the daemon).
- Community health files: `SECURITY.md`, `CONTRIBUTING.md` (with the `ConsistencyTests`
  anti-drift contract and the salvaged known pitfalls), `CODE_OF_CONDUCT.md`, and
  issue/PR templates.
- GitHub release workflow (`.github/workflows/release.yml`): tag-triggered, builds +
  tests + bundles the `.mcpb` and attaches it to a GitHub Release.

### Dev-only files moved out of the user surface
- `HANDOFF.md` removed (pitfalls salvaged into `CONTRIBUTING.md`);
  `WINDOWS-VALIDATION.md` moved under `dev/` as a point-in-time internal QA log.

### Fixes
- `FileSendAsync` (CETBridge file transport) no longer deletes `response.json`
  before validating it: a partial read of an in-flight atomic rename used to
  destroy the real response and force a full timeout. This removed a flaky
  `FileSendTests` failure on loaded CI runners.

### Counts
- 123 tools (63 base + 25 workflow + 35 live), 8 prompts, 4 resources — unchanged
  from 0.4.0; the first release tagged for distribution.

## 0.4.0 — 2026-06-15

Finalization: full audit of the server (deemed functionally complete), fix of a
documentation drift, a safeguard against its recurrence, and three small utility
tools. **120 → 123 tools** (63 base + 25 workflow + 35 live).

### New tools (offline, deterministic, tested)
- **`archive_stats`** — breakdown of a `.archive`'s content by extension
  (how many `.mesh`, `.ent`, `.xbm`…), over the LRU listing cache.
- **`validate_redmod`** — validates a REDmod's `info.json` (required fields
  `name`/`version` + format, consistency of `customSounds` entries). Completes the
  `validate_*` family.
- **`inspect_app`** — structural summary of an `.app` (appearances, mesh components
  per appearance, distinct meshes), overview before `validate_appearance`.

### Anti-drift safeguard
- `ConsistencyTests` only checked the doc → code direction (the cited names
  exist). Two new tests check the **reverse**: the README documents all the
  prompts/resources of the code, and the announced counts (123/8/4) match the
  code. It was this missing direction that had let the README drift (5 prompts /
  3 resources documented instead of 8 / 4).

### Real-game validation
- New `test-new-tools.py`: drives the MCP server over stdio and exercises the three
  tools on a real installation (base archive, installed REDmod, `.app` extracted
  from the game). **6 PASS · 0 FAIL** on Windows 11 on 2026-06-15.
- Bug fixed, detected by this pass: the nested keys `byExtension`
  (`archive_stats`) and `appearances` (`inspect_app`) were coming out in PascalCase
  instead of the envelope's camelCase — projected to lowercase at serialization time.

### Documentation resynchronized
- README: Prompts (5 → 8) and Resources (3 → 4) tables completed, "Exposed tools"
  header disambiguated, total raised to 123 everywhere.
- `docs/TOOLS.md`, `docs/ARCHITECTURE.md`, `docs/LIVE_BRIDGE.md`,
  `docs/HTTP_TRANSPORT.md`, `manifest.json`, `HANDOFF.md` aligned on 123.

## 0.3.0 — 2026-06-10

Quality work resulting from a full audit of the server.

### MCP protocol better exploited
- **Tool annotations** on the 120 tools (`readOnlyHint`, `destructiveHint`,
  `idempotentHint`): clients can auto-approve the ~55 read-only tools and
  ask for confirmation for the destructive ones (`uninstall_*`,
  `write_game_file`, `live_kill_nearby`…). Verified by E2E test.
- **Progress notifications** on long tools (`uncook`, `extract_files`,
  `export_files`, `extract_audio`, `wwise_export`, `build_project`): the daemon
  relays its log lines (throttled to 2/s) and WolvenKit percentages as
  `notifications/progress` messages instead of staying silent for minutes.
- **Resource `wkmcp://mods/{gamePath}`**: inventory of installed mods
  (archives, REDmods, tweaks, scripts).
- **3 new prompts**: `create_archivexl_item` (validated record→factory→loc
  chain), `diagnose_broken_mod` (from doctor to bisection),
  `live_iteration_loop` (live TweakDB iteration then frozen into a .tweak).

### Consistency (fixes texts that misinformed the agent)
- The `wkmcp://reference` resource is now **generated by reflection**
  from the real tools — counts and names can no longer be stale
  (it announced "53 tools" for 120 real ones).
- `edit_tweakdb_item` prompt fixed: it claimed that `write_tweak`/
  `install_tweak` did not yet exist and recommended manual editing.
- `manifest.json`: up-to-date description (120 tools), `user_config` finally **wired**
  into the server's env (`cli_timeout_seconds` was declared but never passed),
  added `cet_tcp_port` and `cet_bridge_timeout_ms`.
- Anti-regression test: any tool name cited in a prompt or the reference must
  exist in the assembly (reflection), and each tool must declare its
  annotations.

### Bounded outputs (protection of the agent's context)
- `find_in_archives`: cap at 500 matches (`maxMatches` parameter),
  total `matchCount` + `truncated` flag.
- `archive_info --list`: cap at 500 files (`maxFiles` parameter).
- `diff_archives`: added/removed lists capped at 500 + real counts.
- `diff_mod_vs_base`: machine-readable counts per category.

### Daemon and runner
- **TweakDB mtime invalidation**: a tweakdb.bin regenerated out-of-band is
  reloaded (before: only path changes triggered a reload).
- **Per-record TweakDB index** (lazy): `tweakdb-describe` goes from two
  O(N) scans of the flats to O(1) lookups.
- **Per-verb idle timeout**: heavy verbs (uncook/build 900 s,
  export/pack 600 s) are no longer killed at 300 s; progress re-arms the delay —
  a live verb is never interrupted, a frozen verb is.
- **Watchdog**: periodic ping of the idle daemon (`ping` verb, handled out of
  serialization); a frozen daemon is killed proactively instead of costing a
  full timeout on the next request.
- Archive listing cache: mtime **+ size** key.
- Startup purge of temp folders `wkmcp-*`/`wkmcp-*` older than 24 h.

### Live bridge (CETBridge)
- File transport driven by **FileSystemWatcher** (~ms latency) with a safety-net
  poll at 250 ms (instead of a fixed 50 ms poll).
- TCP reconnection: the in-flight requests of the old connection fail with an
  explicit "connection replaced — retry" error, without touching those of the
  new connection.
- `live_observe`/`live_observations`: subscriptions are addressable by stable
  label `Class/Event` (server-side registry, Lua mod unchanged).

### Diagnostics
- `analyze_conflicts`: actionable resolution hints (`resolutionHints`).
- `analyze_dependencies`: inter-mod REDscript imports are **resolved against
  the modules actually declared** in r6/scripts — we know which installed mod
  provides each import, or that it is missing.

### Tests and CI
- 114 tests (92 → 114) including an **MCP E2E smoke test**: launches the compiled
  stdio server, `initialize` handshake, paginates `tools/list`, verifies the 120
  tools, the annotations, and that no internal parameter (IProgress) leaks into the
  schemas. Runs in CI without the game or cp77tools.
- CI: the `.mcpb` bundle is unzipped and validated (parsable manifest, entry point,
  native binaries, Lua mod) before upload.
- `build-mcpb.ps1`: UTF-8 BOM (also runnable under Windows PowerShell 5.1).

### Deliberately set aside
- Generalized `structuredContent`: duplicates each response (`content` +
  `structuredContent`) — a token cost with no client gain today.
- AOT/trimming: incompatible with tool discovery by reflection.
- ReadyToRun: the server starts in ~0.4 s (measured); the real cost is the
  loading of the HashService data on the daemon side, which R2R does not improve.
- "batch" daemon verb: `cr2w_to_json`/`uncook`/`export_files` already accept
  folders.

## 0.2.0 — 2026-05-29 → 2026-06-05

A large feature batch that took the server from 60 to 120 tools: the HTTP
transport, the live in-game bridge, and many workflow / appearance / maintenance
tools. Counts below are point-in-time.

### Transports
- Opt-in **HTTP/Streamable** transport (`WKMCP_TRANSPORT=http`),
  SDK 1.3.0 → 1.4.0, loopback bind by default, bearer token (constant-time
  SHA-256), fail-closed off loopback without a token. `docs/HTTP_TRANSPORT.md`.

### Live in-game bridge (CETBridge)
- 35 `live_*` tools talking to a running game via the **CETBridge** Lua mod
  (TCP 127.0.0.1:27010 + file fallback). 120 tools.

### Creation / maintenance tools
- Appearance hardening: `list_entity_appearances`, `validate_appearance` (85 tools).
- Advanced creation/maintenance: `lint_tweak`, `generate_manifest`,
  `resolve_dynamic_appearance`, `migration_check`, `toggle_mods`,
  `export_entity` / `export_materials` (83 tools).
- Generic CR2W navigation: `inspect_cr2w` / `find_in_cr2w`, `diagnose_logs`,
  `analyze_conflicts`, `validate_item_mod` (76 tools).
- Journal intelligence: `inspect_journal` / `find_journal_entry` (28,476 entries,
  71 tools).
- 9 workflow tools (`ModdingTools.cs`): `analyze_dependencies`, `mod_doctor`,
  `validate_xl`, `scaffold_*`, `diff_mod_vs_base`, `find_references`,
  `package_mod`. Dedicated REDscript parser for `lint_script` (0 false positives on
  the 1374 `.reds` of the game). 69 tools.
- Localization & audio: `loc_resolve`, `import_audio`, `extract_audio` (82k opus
  verified), generated `.cpmodproj` (`build_project` end-to-end). 60 tools.

### Hardening
- TweakDB singleton fixed (per-path reload), Zip-Slip in `restore_mods`, temp
  collisions. `docs/` documentation added.

## 0.1.0 — 2026-05-19/20

- C#/.NET 8 MCP server + persistent WolvenKit daemon (HashService loaded once,
  pipelined stdio IPC, cp77tools subprocess fallback), LRU cache of listings,
  structured JSON output, per-verb metrics.
- Windows validation on a real installation (`validate-windows.py`):
  53 tools, 5 prompts, 3 resources — 62 OK / 1 reservation / 1 partial / 0 failures.
- Claude Desktop `.mcpb` bundle (`build-mcpb.ps1`), GitHub Actions CI.
