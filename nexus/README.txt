================================================================================
  WkMCP — AI-assisted Cyberpunk 2077 modding (Model Context Protocol server)
================================================================================

WkMCP is NOT an in-game mod. It is a tool that gives an AI agent (Claude) the
WolvenKit toolkit so you can mod Cyberpunk 2077 from a chat window: read/edit
game files, query and patch the TweakDB, pack and install mods, export
meshes/textures, lint REDscript, diagnose a broken install, and optionally drive
a *running* game live.

The server itself is a compiled .NET program. Nexus does not host executables,
so this Nexus download contains ONLY text assets. Get the actual server below.

--------------------------------------------------------------------------------
  1. Download the server bundle (.mcpb) — from GitHub
--------------------------------------------------------------------------------

The one-click Desktop Extension bundle (wkmcp.mcpb) and its checksum are attached
to every GitHub Release:

    https://github.com/Guikingone/WkMCP/releases/latest

Install it in Claude Desktop: Settings -> Extensions (Developer Mode) -> install
the .mcpb. Requires Windows + the .NET 8 runtime. Full instructions and the
build-from-source path are in the repository README.

--------------------------------------------------------------------------------
  2. (Optional) In-game live bridge — CETBridge
--------------------------------------------------------------------------------

The CETBridge/ folder in this archive is the optional in-game half: a Cyber
Engine Tweaks (CET) mod that lets WkMCP's live_* tools talk to a running game
(read/teleport/spawn/observe). It is plain Lua — no binaries.

To install it, copy the CETBridge folder into:

    <Cyberpunk 2077>\bin\x64\plugins\cyber_engine_tweaks\mods\CETBridge

Then launch the game with CET installed. You do not need this for the offline
tools (archives, TweakDB, packing, etc.) — only for the live in-game features.

--------------------------------------------------------------------------------
  License & source
--------------------------------------------------------------------------------

GPL-3.0. The server links the GPL-3.0 WolvenKit libraries; the MCP server source
itself is MIT. Full source, NOTICE and per-component attribution:

    https://github.com/Guikingone/WkMCP

Credits: WolvenKit (the modding toolkit this wraps); Cyber Engine Tweaks;
CETBridge by y4rd13; the open-source ooz Kraken codec (powzix/rarten) — no
proprietary Oodle is redistributed.
