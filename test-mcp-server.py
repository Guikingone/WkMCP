#!/usr/bin/env python3
"""
Test client for the WolvenKit MCP server (stdio transport / JSON-RPC 2.0).

Runs the server, performs the MCP handshake, lists the exposed tools, then
calls read tools (wolvenkit_status, compute_hash), a write tool
(pack_archive) and a workflow tool (create_mod_project). Each
response is awaited before sending the next request.

Usage: python3 test-mcp-server.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

# UTF-8 output: this script prints non-ASCII characters (•, ✓, accents,
# server output). Without this, the Windows console (cp1252) crashes print().
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DLL = os.environ.get(
    "WOLVENKIT_MCP_DLL",
    os.path.join(HERE, "src", "WolvenKitMcp", "bin", "Debug", "net8.0", "WolvenKitMcp.dll"))
# dotnet: from PATH (Windows, Linux), otherwise Homebrew macOS fallback.
DOTNET = (os.environ.get("WOLVENKIT_DOTNET")
          or shutil.which("dotnet")
          or "/opt/homebrew/opt/dotnet@8/libexec/dotnet")

# tempfile.gettempdir() is cross-platform: /tmp on Unix, %TEMP% on Windows.
TMP = tempfile.gettempdir()
PACK_SRC = os.path.join(TMP, "wkpack_src")
PACK_OUT = os.path.join(TMP, "wkpack_out")


def prepare_pack_fixture():
    """Creates a real source folder to exercise the pack_archive write tool."""
    shutil.rmtree(PACK_SRC, ignore_errors=True)
    shutil.rmtree(PACK_OUT, ignore_errors=True)
    d = os.path.join(PACK_SRC, "base", "wolvenkit_mcp_test")
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "sample.txt"), "w") as f:
        f.write("WolvenKit MCP pack test - compressible content. " * 200)
    os.makedirs(PACK_OUT, exist_ok=True)


def main():
    if not os.path.exists(SERVER_DLL):
        sys.exit(f"Server DLL not found: {SERVER_DLL}\n"
                 "Build first: dotnet build src/WolvenKitMcp")

    env = dict(os.environ)

    proc = subprocess.Popen(
        [DOTNET, SERVER_DLL],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        encoding="utf-8", errors="replace", bufsize=1, env=env)

    def send(obj):
        proc.stdin.write(json.dumps(obj) + "\n")
        proc.stdin.flush()

    def recv():
        line = proc.stdout.readline()
        if not line.strip():
            return None
        try:
            return json.loads(line)
        except json.JSONDecodeError:
            print(f"  [non-JSON stdout line: {line.strip()[:200]}]")
            return None

    def tool_text(resp):
        if resp is None:
            return "(no response)"
        if "error" in resp:
            return f"JSON-RPC ERROR: {resp['error']}"
        result = resp.get("result", {})
        texts = [c.get("text", "") for c in result.get("content", [])
                 if c.get("type") == "text"]
        suffix = "   [isError=true]" if result.get("isError") else ""
        return ("\n".join(texts) or "(empty content)") + suffix

    def resource_text(resp):
        if resp is None:
            return "(no response)"
        if "error" in resp:
            return f"JSON-RPC ERROR: {resp['error']}"
        contents = resp.get("result", {}).get("contents", [])
        return "\n".join(c.get("text", "") for c in contents) or "(empty content)"

    # 1) handshake: initialize
    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                     "clientInfo": {"name": "wolvenkit-mcp-tester", "version": "0.1"}}})
    init = recv()
    res = (init or {}).get("result", {})
    print(f"=== initialize ===\n  server    : {res.get('serverInfo')}"
          f"\n  protocol  : {res.get('protocolVersion', '?')}")

    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    # 2) tools/list
    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
    tools = (recv() or {}).get("result", {}).get("tools", [])
    print(f"\n=== tools/list — {len(tools)} tool(s) ===")
    for t in tools:
        print(f"  • {t['name']}")

    # 3) tools/call: wolvenkit_status (read)
    send({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
          "params": {"name": "wolvenkit_status", "arguments": {}}})
    print("\n=== tools/call: wolvenkit_status ===")
    print(tool_text(recv()))

    # 4) tools/call: compute_hash (read)
    send({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
          "params": {"name": "compute_hash",
                     "arguments": {"inputs": ["WolvenKit", "base", "test.mesh"]}}})
    print("\n=== tools/call: compute_hash(['WolvenKit','base','test.mesh']) ===")
    print(tool_text(recv()))

    # 5) tools/call: pack_archive (write — exercises native Kraken compression)
    prepare_pack_fixture()
    send({"jsonrpc": "2.0", "id": 5, "method": "tools/call",
          "params": {"name": "pack_archive",
                     "arguments": {"folderPath": PACK_SRC, "outputPath": PACK_OUT}}})
    print(f"\n=== tools/call: pack_archive({PACK_SRC} → {PACK_OUT}) ===")
    print(tool_text(recv()))
    produced = ([f for f in os.listdir(PACK_OUT) if f.endswith(".archive")]
                if os.path.isdir(PACK_OUT) else [])
    print(f"  → .archive files produced: {produced or 'none'}")

    # 6) tools/call: create_mod_project (workflow — filesystem, without cp77tools)
    proj_parent = os.path.join(TMP, "wkproj")
    shutil.rmtree(proj_parent, ignore_errors=True)
    os.makedirs(proj_parent, exist_ok=True)
    send({"jsonrpc": "2.0", "id": 6, "method": "tools/call",
          "params": {"name": "create_mod_project",
                     "arguments": {"parentFolder": proj_parent, "modName": "MonMod"}}})
    print(f"\n=== tools/call: create_mod_project({proj_parent}, 'MonMod') ===")
    print(tool_text(recv()))
    created = os.path.join(proj_parent, "MonMod", "source", "archive")
    print(f"  → source/archive structure created: {os.path.isdir(created)}")

    # 7) resources/list — direct resources
    send({"jsonrpc": "2.0", "id": 7, "method": "resources/list", "params": {}})
    resources = (recv() or {}).get("result", {}).get("resources", [])
    print(f"\n=== resources/list — {len(resources)} resource(s) ===")
    for r in resources:
        print(f"  • {r.get('uri')}  ({r.get('name')})")

    # 8) resources/templates/list — resource templates
    send({"jsonrpc": "2.0", "id": 8, "method": "resources/templates/list", "params": {}})
    templates = (recv() or {}).get("result", {}).get("resourceTemplates", [])
    print(f"\n=== resources/templates/list — {len(templates)} template(s) ===")
    for t in templates:
        print(f"  • {t.get('uriTemplate')}  ({t.get('name')})")

    # 9) resources/read — static resource
    send({"jsonrpc": "2.0", "id": 9, "method": "resources/read",
          "params": {"uri": "wolvenkit://reference"}})
    print("\n=== resources/read: wolvenkit://reference (excerpt) ===")
    print(resource_text(recv())[:400])

    # 10) resources/read — resource template (archive produced in step 5)
    arch = os.path.join(PACK_OUT, "wkpack_src.archive")
    send({"jsonrpc": "2.0", "id": 10, "method": "resources/read",
          "params": {"uri": f"wolvenkit://archive/{arch}"}})
    print(f"\n=== resources/read: wolvenkit://archive/{arch} ===")
    print(resource_text(recv()))

    # 11) prompts/list — MCP recipes
    send({"jsonrpc": "2.0", "id": 11, "method": "prompts/list", "params": {}})
    prompts = (recv() or {}).get("result", {}).get("prompts", [])
    print(f"\n=== prompts/list — {len(prompts)} prompt(s) ===")
    for p in prompts:
        print(f"  • {p.get('name')}")

    # 12) prompts/get — runs through a recipe (read_game_file_workflow)
    if any(p.get("name") == "read_game_file_workflow" for p in prompts):
        send({"jsonrpc": "2.0", "id": 12, "method": "prompts/get",
              "params": {"name": "read_game_file_workflow",
                         "arguments": {"filePattern": "*.ent",
                                       "contentFolder": "C:\\game\\archive\\pc\\content"}}})
        rp = recv() or {}
        msgs = rp.get("result", {}).get("messages", [])
        print(f"\n=== prompts/get: read_game_file_workflow — {len(msgs)} message(s) ===")
        for m in msgs[:1]:
            content = m.get("content", {})
            text = content.get("text", "") if isinstance(content, dict) else str(content)
            print(text[:300] + ("…" if len(text) > 300 else ""))

    proc.stdin.close()
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.kill()

    err = (proc.stderr.read() or "").strip()
    if err:
        timings = [ln.strip() for ln in err.splitlines() if "completed in" in ln]
        if timings:
            print("\n=== Server latencies (request handler) ===")
            for t in timings:
                print("  " + t)


if __name__ == "__main__":
    main()
