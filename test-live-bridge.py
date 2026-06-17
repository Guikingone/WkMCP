#!/usr/bin/env python3
"""MANUAL smoke test of the live in-game bridge (CETBridge mod), via the file transport.

Does NOT require the MCP server: this script plays the role of the server by writing
command.json directly into the mod folder and reading response.json — which
validates the CET + CETBridge mod + file transport chain end to end.

Prerequisites: Cyberpunk 2077 RUNNING, with CET + the CETBridge mod installed in
  <game>/bin/x64/plugins/cyber_engine_tweaks/mods/CETBridge/

Usage:
  python test-live-bridge.py "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Cyberpunk 2077"
"""
import json
import os
import sys
import time
import uuid


def bridge_dir(game_path: str) -> str:
    return os.path.join(game_path, "bin", "x64", "plugins",
                        "cyber_engine_tweaks", "mods", "CETBridge")


def send(bdir: str, req: dict, timeout_s: float = 5.0) -> dict:
    cmd = os.path.join(bdir, "command.json")
    tmp = os.path.join(bdir, "command.json.tmp")
    res = os.path.join(bdir, "response.json")

    if os.path.exists(res):
        os.remove(res)
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(req, f)
    os.replace(tmp, cmd)  # atomic write (like the C# server)

    deadline = time.time() + timeout_s
    while time.time() < deadline:
        if os.path.exists(res):
            try:
                with open(res, "r", encoding="utf-8") as f:
                    data = json.load(f)
                os.remove(res)
                if data.get("id") == req["id"]:
                    return data
            except (json.JSONDecodeError, OSError):
                pass  # write in progress: retry
        time.sleep(0.05)
    raise TimeoutError(f"No response in {timeout_s}s — is the game running with CETBridge?")


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    bdir = bridge_dir(sys.argv[1])
    if not os.path.isdir(bdir):
        print(f"[FAIL] Mod folder not found: {bdir}")
        return 1

    hb = os.path.join(bdir, "heartbeat.json")
    print(f"[i] Mod folder: {bdir}")
    print(f"[i] heartbeat.json present: {os.path.exists(hb)} "
          f"(the mod should write it ~1x/s)")

    checks = [
        ("eval 1+1", {"type": "eval", "expr": "1 + 1"}),
        ("player level", {"type": "eval", "expr": "Game.GetPlayer() and "
                          "Game.GetStatsSystem():GetStatValue("
                          "Game.GetPlayer():GetEntityID(), gamedataStatType.Level)"}),
        ("query player_info", {"type": "query", "handler": "player_info"}),
    ]
    ok = 0
    for label, payload in checks:
        req = {"id": uuid.uuid4().hex, **payload}
        try:
            resp = send(bdir, req)
            status = "OK " if resp.get("ok") else "ERR"
            print(f"[{status}] {label}: {resp.get('result') or resp.get('error')}")
            ok += 1 if resp.get("ok") else 0
        except TimeoutError as e:
            print(f"[FAIL] {label}: {e}")
            return 1
    print(f"\n{ok}/{len(checks)} succeeded.")
    return 0 if ok == len(checks) else 1


if __name__ == "__main__":
    raise SystemExit(main())
