#!/usr/bin/env python3
"""
Validation jeu réel des trois outils ajoutés en 0.4.0 :
  • archive_stats   — histogramme par extension d'une .archive
  • validate_redmod — schéma info.json d'un REDmod
  • inspect_app     — résumé structurel d'un .app

Pilote le serveur MCP en stdio (JSON-RPC 2.0), comme test-mcp-server.py /
validate-windows.py. Chaque outil est exercé avec de vraies données du jeu
(archives de base, .app extrait) plus, pour validate_redmod, un cas valide ET un
cas volontairement cassé.

Usage :
    python test-new-tools.py "C:\\chemin\\vers\\Cyberpunk 2077"
  ou  set WOLVENKIT_GAME=...  puis  python test-new-tools.py

Le chemin de la DLL serveur est pris dans WOLVENKIT_MCP_DLL, sinon le build
Release (généré par build-mcpb.ps1), sinon le build Debug.

Code de sortie : 0 si tous les cas PASS, 1 sinon.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))


def _first_existing(*paths):
    for p in paths:
        if p and os.path.exists(p):
            return p
    return paths[-1]


SERVER_DLL = os.environ.get("WOLVENKIT_MCP_DLL") or _first_existing(
    os.path.join(HERE, "src", "WolvenKitMcp", "bin", "Release", "net8.0", "WolvenKitMcp.dll"),
    os.path.join(HERE, "src", "WolvenKitMcp", "bin", "Debug", "net8.0", "WolvenKitMcp.dll"))
DOTNET = os.environ.get("WOLVENKIT_DOTNET") or shutil.which("dotnet") or "dotnet"

GAME = (sys.argv[1] if len(sys.argv) > 1
        else os.environ.get("WOLVENKIT_GAME", r"C:\Cyberpunk\Cyberpunk 2077")).rstrip("\\/")
CONTENT = os.path.join(GAME, "archive", "pc", "content")
MODS_DIR = os.path.join(GAME, "mods")  # REDmods installés (format post-1.6)

# Archive de base riche en types de fichiers (meshes + textures) → bon histogramme.
ARCH_ENGINE = os.path.join(CONTENT, "basegame_1_engine.archive")

WORK = os.path.join(tempfile.gettempdir(), "wknewtools")


# ── Client MCP minimal ───────────────────────────────────────────────────────
class Server:
    def __init__(self):
        self.proc = subprocess.Popen(
            [DOTNET, SERVER_DLL],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            encoding="utf-8", errors="replace", bufsize=1)
        self._id = 0

    def _send(self, obj):
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()

    def _recv(self):
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise IOError("le serveur MCP a fermé stdout")
            line = line.strip()
            if line:
                return json.loads(line)

    def request(self, method, params):
        self._id += 1
        rid = self._id
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        while True:
            resp = self._recv()
            if resp.get("id") == rid:
                return resp

    def initialize(self):
        r = self.request("initialize", {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": "wk-newtools", "version": "1.0"}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        return r

    def call(self, name, args):
        r = self.request("tools/call", {"name": name, "arguments": args})
        if "error" in r:
            return {"ok": False, "status": "error", "errors": [str(r["error"])],
                    "summary": f"JSON-RPC : {r['error']}"}
        res = r.get("result", {})
        txt = "\n".join(c.get("text", "") for c in res.get("content", [])
                        if c.get("type") == "text")
        try:
            return json.loads(txt)
        except json.JSONDecodeError:
            return {"ok": False, "status": "error", "errors": [txt[:300]],
                    "summary": txt[:300] or "(réponse vide)"}

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=15)
        except Exception:
            self.proc.kill()


# ── Résultats ────────────────────────────────────────────────────────────────
results = []
MARK = {"PASS": "✓", "WARN": "!", "FAIL": "✗", "SKIP": "–"}


def record(target, status, detail):
    results.append((target, status, detail))
    print(f"[{MARK.get(status, '?')}] {status:5} {target} — {detail}")


def find_extracted(root, suffix):
    for dirpath, _, files in os.walk(root):
        for f in files:
            if f.lower().endswith(suffix):
                return os.path.join(dirpath, f)
    return None


# ── Tests ────────────────────────────────────────────────────────────────────
def test_archive_stats(srv):
    if not os.path.isfile(ARCH_ENGINE):
        record("archive_stats", "SKIP", f"archive absente : {ARCH_ENGINE}")
        return
    res = srv.call("archive_stats", {"archivePath": ARCH_ENGINE})
    ok = (res.get("ok") and res.get("fileCount", 0) > 0
          and res.get("categoryCount", 0) > 1 and res.get("byExtension"))
    top = ", ".join(f"{e['extension']}={e['count']}" for e in res.get("byExtension", [])[:5])
    record("archive_stats", "PASS" if ok else "FAIL",
           f"{res.get('fileCount')} fichiers, {res.get('categoryCount')} types — top : {top}")

    # 2e appel : doit venir du cache LRU.
    res2 = srv.call("archive_stats", {"archivePath": ARCH_ENGINE})
    record("archive_stats (cache chaud)", "PASS" if res2.get("fromCache") else "WARN",
           f"fromCache={res2.get('fromCache')}")


def test_validate_redmod(srv):
    # a) cas valide : projet REDmod scaffoldé par l'outil.
    parent = os.path.join(WORK, "redmod_valid")
    shutil.rmtree(parent, ignore_errors=True)
    os.makedirs(parent, exist_ok=True)
    res = srv.call("create_redmod_project",
                   {"parentFolder": parent, "modName": "ValidationRedmod", "version": "1.0.0"})
    root = res.get("redmodRoot") or os.path.join(parent, "ValidationRedmod")
    if not res.get("ok") or not os.path.isfile(os.path.join(root, "info.json")):
        record("validate_redmod (valide)", "FAIL", f"scaffold KO : {res.get('summary')}")
    else:
        v = srv.call("validate_redmod", {"modPath": root})
        ok = v.get("ok") and v.get("status") == "success" and not v.get("errors")
        record("validate_redmod (valide)", "PASS" if ok else "FAIL",
               f"status={v.get('status')}, {len(v.get('errors', []))} erreur(s)")

    # b) cas cassé : info.json sans champ version.
    broken = os.path.join(WORK, "redmod_broken")
    shutil.rmtree(broken, ignore_errors=True)
    os.makedirs(broken, exist_ok=True)
    with open(os.path.join(broken, "info.json"), "w", encoding="utf-8") as f:
        json.dump({"name": "Cassé", "description": "sans version"}, f)
    v = srv.call("validate_redmod", {"modPath": broken})
    ok = (not v.get("ok")) and any("version" in e for e in v.get("errors", []))
    record("validate_redmod (cassé)", "PASS" if ok else "FAIL",
           f"détecte l'erreur version : {ok} ({len(v.get('errors', []))} erreur(s))")

    # c) bonus : premier REDmod installé, s'il y en a un.
    installed = ([d for d in os.listdir(MODS_DIR)
                  if os.path.isfile(os.path.join(MODS_DIR, d, "info.json"))]
                 if os.path.isdir(MODS_DIR) else [])
    if installed:
        target = os.path.join(MODS_DIR, installed[0])
        v = srv.call("validate_redmod", {"modPath": target})
        record(f"validate_redmod ({installed[0]})",
               "PASS" if v.get("status") in ("success", "partial") else "FAIL",
               f"status={v.get('status')}, {len(v.get('errors', []))} erreur(s)")
    else:
        record("validate_redmod (REDmod installé)", "SKIP", "aucun REDmod dans mods/")


def test_inspect_app(srv):
    if not os.path.isdir(CONTENT):
        record("inspect_app", "SKIP", f"dossier content absent : {CONTENT}")
        return
    # 1) localiser un .app dans les archives de base.
    found = srv.call("find_in_archives", {"archivesFolder": CONTENT,
                                          "pattern": "*.app", "maxMatches": 5})
    matches = found.get("matches", [])
    if not matches:
        record("inspect_app", "SKIP", "aucun .app trouvé dans le contenu de base")
        return
    # matches[i] = "chemin\\interne.app  (nom_archive.archive)"
    internal, _, arch_part = matches[0].partition("  (")
    internal = internal.strip()
    archive = os.path.join(CONTENT, arch_part.rstrip(") ").strip())
    basename = internal.replace("/", "\\").split("\\")[-1]

    # 2) extraire ce .app.
    extract_dir = os.path.join(WORK, "appextract")
    shutil.rmtree(extract_dir, ignore_errors=True)
    os.makedirs(extract_dir, exist_ok=True)
    srv.call("extract_files", {"archivePath": archive, "outputPath": extract_dir,
                               "pattern": "*" + basename})
    app_file = find_extracted(extract_dir, ".app")
    if not app_file:
        record("inspect_app", "FAIL", f"extraction de {basename} échouée depuis {arch_part}")
        return

    # 3) inspecter.
    res = srv.call("inspect_app", {"appFile": app_file})
    ok = res.get("ok") and res.get("appearanceCount", 0) >= 1
    record("inspect_app", "PASS" if ok else "FAIL",
           f"{basename} → {res.get('appearanceCount')} apparence(s), "
           f"{res.get('meshComponentCount')} composant(s) mesh, "
           f"{res.get('distinctMeshCount')} mesh(es) distinct(s)")


def main():
    if not os.path.exists(SERVER_DLL):
        sys.exit(f"DLL serveur introuvable : {SERVER_DLL}\n"
                 "Compiler d'abord : dotnet build src\\WolvenKitMcp -c Release")
    if not os.path.isdir(GAME):
        sys.exit(f"Installation Cyberpunk 2077 introuvable : {GAME}\n"
                 "Passer le chemin en argument ou via WOLVENKIT_GAME.")

    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK, exist_ok=True)
    print(f"=== Test des outils 0.4.0 — jeu : {GAME} ===")
    print(f"    serveur : {SERVER_DLL}\n")

    srv = Server()
    try:
        srv.initialize()
        test_archive_stats(srv)
        test_validate_redmod(srv)
        test_inspect_app(srv)
    finally:
        srv.close()

    passed = sum(1 for _, s, _ in results if s == "PASS")
    failed = sum(1 for _, s, _ in results if s == "FAIL")
    skipped = sum(1 for _, s, _ in results if s == "SKIP")
    warned = sum(1 for _, s, _ in results if s == "WARN")
    print(f"\n=== Bilan : {passed} PASS · {failed} FAIL · {warned} WARN · {skipped} SKIP ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
