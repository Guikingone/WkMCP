# Scene (.scene) support

`.scene` files (`scnSceneResource`) are Cyberpunk 2077's quest/dialogue scene system: a
graph of nodes (sections that play timelines of events, player choices, hubs, quest
bridges, logic gates…) wired together by **output→input sockets**, with dialogue split
across a *screenplay store* (logical lines/options + who speaks) and an embedded *loc
store* (the subtitle text).

Until now they were only reachable through the generic `inspect_cr2w` / `find_in_cr2w`,
which know nothing about the graph or the dialogue. These tools make scenes first-class —
inspect, graph, navigate, validate, and translate them **headlessly** (no GUI, no game),
via the daemon's `convert serialize/deserialize`. Each tool accepts a `.scene` (converted
internally) or a `.json` already produced by `read_game_file` / `cr2w_to_json`.

## Tools

| Tool | What it does |
|------|--------------|
| `inspect_scene` | Structural summary: node count + histogram by `$type`, #actors/playerActors, #screenplay lines/options, entry/exit/notable points, start/end node ids, `version`. |
| `scene_graph` | The narrative flow: nodes `{id, type, label, choices}` + edges `{from, fromSocket, to}` from the output sockets; choice nodes list their option captions. Bounded (`maxNodes`) with a `truncated` flag. |
| `find_in_scene` | Locate by `field` = `text` (choice captions + resolved dialogue), `id` (node/screenplay id), or `type` (node `$type`). Returns JSON paths for `read_game_file`/`write_game_file`. |
| `validate_scene` | Graph + dialogue integrity (see below). |
| `extract_scene_localization` | Dump dialogue (lines + choice options) to `{ "<ruid>": { text, speaker, kind } }` per locale — for analysis or translation. |
| `apply_scene_localization` | Write an edited translations JSON back into the embedded loc store and re-serialize to a `.scene`, with a control round-trip. |
| `scene_dependencies` | List the scene's **external** references — `.anims` (from `resouresReferences`), `ridResources`, prop `.ent`, actor TweakDB records — and optionally resolve them against a `modRoot`. |
| `scene_events` | Per-section **timeline**: dialogue (resolved text + speaker), animation, audio, camera/VFX, with startTime/duration. |
| `scene_set_actor` | Retarget an actor's `specCharacterRecordId` / `specAppearance` by id (re-serialize + round-trip). |
| `scene_replace_resource` | Swap a depot path everywhere it appears (e.g. a `.anims`) (re-serialize + round-trip). |
| `scaffold_scene` | Generate a minimal valid scene skeleton (start → N sections → end) → `json_to_cr2w`. |

The prompts **`translate_scene`** (extract → edit → apply → validate) and **`audit_scene`**
(structure → integrity → dependencies → events) walk an agent through the common flows.

## Dependencies, events, editing & scaffolding

`validate_scene` checks the scene's **internal** consistency; **`scene_dependencies`** checks the
**external** side — the resources a scene needs to actually run. A real scene references dozens of
them (animation `.anims` consolidated in `resouresReferences`'s anim-set collections,
`ridResources`, prop `.ent`, and each actor's TweakDB character record). A scene can be internally
valid yet fail in-game because one of these is missing; pass `modRoot` to flag the references your
mod must ship but doesn't (base-game-prefixed paths — `base\`, `ep1\`, `dlc\` — are assumed
present). **`scene_events`** shows what actually plays per section.

For edits beyond localization, **`scene_set_actor`** and **`scene_replace_resource`** mutate the
JSON and re-serialize with the same control round-trip as `apply_scene_localization`. To start from
nothing, **`scaffold_scene`** emits a minimal valid `scnSceneResource` (auto node ids + wired
sockets) that converts to a `.scene` via `json_to_cr2w` and opens in WolvenKit's scene editor — it
is a *skeleton generator*, not a full authoring suite (adding rich nodes by hand means computing
node ids/socket stamps yourself; the GUI editor does that for you).

## What `validate_scene` checks

- **Graph**: non-empty; unique `nodeId`; `startNodes`/`endNodes` resolve and at least one
  `scnStartNode`/`scnEndNode` exists; every output-socket destination resolves to an
  existing node; reachability from a start node (orphans → warning).
- **Actors**: every actor referenced by dialogue/screenplay resolves to `actors[]` /
  `playerActors[]`.
- **Dialogue**: every `scnDialogLineEvent.screenplayLineId` → a screenplay line; every
  choice option → a screenplay option; every `locstringId` resolves to **non-empty embedded
  text** (when the scene embeds its text); choice option count vs. live output sockets.

Two real gotchas are handled so they are **not** reported as errors:

- the `scnCutControlNode` failsafe **backup socket** (`stamp.name = 1026`) looks dangling
  but is intentional → whitelisted;
- `scnDeletionMarkerNode` are intentional tombstones that keep ids stable for save games →
  never flagged (only a *live edge into* one is a warning).

## Translating a scene

```
extract_scene_localization scene.scene  →  translations.json   { "<ruid>": { text, speaker } }
        │  edit the text values
        ▼
apply_scene_localization scene.scene translations.json  →  scene.scene (translated)
        ▼  control round-trip: re-reads the output and warns if a string didn't survive
validate_scene  →  pack_archive → install_mod
```

**Caveat:** dialogue text lives in the scene's *embedded* loc store. Many scenes (e.g. the
Native Interactions scenes) carry **no** embedded text — their strings are resolved from the
game's external localization by `ruid`. For those, `extract_scene_localization` returns
entries with `text = null` and `apply_scene_localization` writes nothing (it tells you so):
they can't be translated by editing the scene. And because WolvenKit can re-serialize some
`scnSceneResource` content imperfectly, `apply_scene_localization` performs a round-trip and
warns if an edit did not survive — verify those in the GUI/in-game.

## Notes

- Fully offline; no game install required. (A real-scene smoke test runs when
  `WKMCP_TEST_SCENE` points at a `cr2w_to_json` output.)
- The node-type knowledge is pinned to WolvenKit 8.18 (14 concrete `scn*` node types). New
  node types added by a future game/WolvenKit version still appear in `inspect_scene` /
  `scene_graph` (by `$type`) — only type-specific niceties would need an update.
