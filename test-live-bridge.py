#!/usr/bin/env python3
"""Smoke test MANUEL du pont live in-game (mod CETBridge), via le transport fichier.

Ne requiert PAS le serveur MCP : ce script joue le rôle du serveur en écrivant
directement command.json dans le dossier du mod et en lisant response.json — ce qui
valide la chaîne CET + mod CETBridge + transport fichier de bout en bout.

Prérequis : Cyberpunk 2077 LANCÉ, avec CET + le mod CETBridge installé dans
  <jeu>/bin/x64/plugins/cyber_engine_tweaks/mods/CETBridge/

Usage :
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
    os.replace(tmp, cmd)  # écriture atomique (comme le serveur C#)

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
                pass  # en cours d'écriture : on retente
        time.sleep(0.05)
    raise TimeoutError(f"Pas de réponse en {timeout_s}s — le jeu tourne-t-il avec CETBridge ?")


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    bdir = bridge_dir(sys.argv[1])
    if not os.path.isdir(bdir):
        print(f"[FAIL] Dossier du mod introuvable : {bdir}")
        return 1

    hb = os.path.join(bdir, "heartbeat.json")
    print(f"[i] Dossier du mod : {bdir}")
    print(f"[i] heartbeat.json présent : {os.path.exists(hb)} "
          f"(le mod doit l'écrire ~1×/s)")

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
    print(f"\n{ok}/{len(checks)} réussis.")
    return 0 if ok == len(checks) else 1


if __name__ == "__main__":
    raise SystemExit(main())
