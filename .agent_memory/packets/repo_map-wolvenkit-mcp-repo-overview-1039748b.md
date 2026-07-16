---
type: "Repo Map"
title: "wolvenkit-mcp repo overview"
description: "README excerpt: WkMCP — an MCP server for Cyberpunk 2077 modding An MCP Model Context Protocol server that exposes WolvenKit's modding CLI cp77tools as 173 tools an agent Claude can call — so you steer Cyberpunk 2077 mod"
resource: "README.md"
tags: ["repo", "overview"]
timestamp: "2026-07-16"
x-kage-id: "repo:https-github-com-guikingone-wolvenkitmcp:repo_map:wolvenkit-mcp-repo-overview-auto-overview"
x-kage-type: "repo_map"
x-kage-status: "approved"
x-kage-scope: "repo"
x-kage-visibility: "team"
x-kage-verified: "verified"
x-kage-paths: ["README.md"]
---

# wolvenkit-mcp repo overview

> README excerpt: WkMCP — an MCP server for Cyberpunk 2077 modding An MCP Model Context Protocol server that exposes Wo…

README excerpt:
# WkMCP — an MCP server for Cyberpunk 2077 modding

An **MCP (Model Context Protocol)** server that exposes WolvenKit's modding CLI
(`cp77tools`) as **173 tools** an agent (Claude) can call — so you steer Cyberpunk
2077 modding from a chat window without writing code: read and edit game files,
query and patch the TweakDB, create/pack/install mods, export meshes and textures,
lint REDscript, diagnose a broken install, and even drive a **running** game live.

**Status: stable v2.0.0 · Windows.** Built on the official `ModelContextProtocol`
SDK 1.4.0 and [WolvenKit](https://github.com/WolvenKit/WolvenKit) 8.19.0; validated end-to-end on Windows 11 with a real
Cyberpunk 2077 install.

## Highlights

- **Read & write game files** — extract from archives, convert CR2W ↔ JSON, export meshes / textures / animations / audio.
- **TweakDB editing** — query `tweakdb.bin`, describe records, generate / validate / install `.tweak` (TweakXL).
- **Create, pack & install mods** — `.archive

## Why

Agents need fast repo orientation before deeper recall or code graph queries, but generated overview memory should stay separate from human rationale.

## Trigger

Recall when an agent needs first-pass repo purpose, scripts, stack, or README context.

## Action

Use this as orientation only, then inspect source-backed memory and code graph facts for implementation decisions.

## Verification

Generated from package.json and README.md when present.

## Risk if forgotten

Agents may waste context rediscovering basic repo purpose or treat generated overview text as deeper semantic memory.

## Stale when

package.json or README.md changes enough that the generated overview no longer matches the repo.

# Citations

[1] file README.md

## Kage state

Machine state for lossless round-trip; OKF consumers can ignore it.

```json kage-state
{"schema_version":2,"id":"repo:https-github-com-guikingone-wolvenkitmcp:repo_map:wolvenkit-mcp-repo-overview-auto-overview","title":"wolvenkit-mcp repo overview","summary":"README excerpt: WkMCP — an MCP server for Cyberpunk 2077 modding An MCP Model Context Protocol server that exposes WolvenKit's modding CLI cp77tools as 173 tools an agent Claude can call — so you steer Cyberpunk 2077 mod","body":"README excerpt:\n# WkMCP — an MCP server for Cyberpunk 2077 modding\r\n\r\nAn **MCP (Model Context Protocol)** server that exposes WolvenKit's modding CLI\r\n(`cp77tools`) as **173 tools** an agent (Claude) can call — so you steer Cyberpunk\n2077 modding from a chat window without writing code: read and edit game files,\r\nquery and patch the TweakDB, create/pack/install mods, export meshes and textures,\r\nlint REDscript, diagnose a broken install, and even drive a **running** game live.\r\n\r\n**Status: stable v2.0.0 · Windows.** Built on the official `ModelContextProtocol`\r\nSDK 1.4.0 and [WolvenKit](https://github.com/WolvenKit/WolvenKit) 8.19.0; validated end-to-end on Windows 11 with a real\nCyberpunk 2077 install.\r\n\r\n## Highlights\r\n\r\n- **Read & write game files** — extract from archives, convert CR2W ↔ JSON, export meshes / textures / animations / audio.\r\n- **TweakDB editing** — query `tweakdb.bin`, describe records, generate / validate / install `.tweak` (TweakXL).\r\n- **Create, pack & install mods** — `.archive","type":"repo_map","scope":"repo","visibility":"team","sensitivity":"internal","status":"approved","confidence":0.75,"tags":["repo","overview"],"paths":["README.md"],"stack":[],"source_refs":[{"kind":"file","path":"README.md"}],"context":{"fact":"Generated repo overview summarizes package metadata and the README as a navigation aid for agent startup.","why":"Agents need fast repo orientation before deeper recall or code graph queries, but generated overview memory should stay separate from human rationale.","trigger":"Recall when an agent needs first-pass repo purpose, scripts, stack, or README context.","action":"Use this as orientation only, then inspect source-backed memory and code graph facts for implementation decisions.","verification":"Generated from package.json and README.md when present.","risk_if_forgotten":"Agents may waste context rediscovering basic repo purpose or treat generated overview text as deeper semantic memory.","stale_when":"package.json or README.md changes enough that the generated overview no longer matches the repo."},"freshness":{"ttl_days":90,"last_verified_at":"2026-07-16","verification":"source_seen"},"edges":[],"quality":{"reviewer":"kage-indexer","votes_up":0,"votes_down":0,"uses_30d":6,"reports_stale":0,"total_uses":6,"last_accessed_at":"2026-07-16T21:16:39.855Z"},"created_at":"2026-07-15T20:24:15.544Z","updated_at":"2026-07-16T21:29:57.112Z"}
```
