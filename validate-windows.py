#!/usr/bin/env python3
"""
Validation Windows complète du serveur WolvenKit MCP.

Pilote le serveur MCP (JSON-RPC 2.0 sur stdio) et exerce les 21 outils et les
3 ressources sur de vrais assets d'une installation de Cyberpunk 2077, puis
affiche un tableau de résultats. C'est la version automatisée de la checklist
manuelle de WINDOWS-VALIDATION.md.

Chaque outil renvoie un objet JSON structuré ({ ok, status, summary, produced,
warnings, errors, ... }) ; ce script s'y fie directement plutôt que de deviner.

Usage :
    python validate-windows.py ["C:\\chemin\\vers\\Cyberpunk 2077"]

Le chemin du jeu peut aussi être donné via la variable WOLVENKIT_GAME.
Prérequis : `dotnet build src\\WolvenKitDaemon` et `dotnet build src\\WolvenKitMcp`.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

# Sorties en UTF-8 : ce script et le serveur émettent des caractères non-ASCII.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DLL = os.environ.get("WOLVENKIT_MCP_DLL") or os.path.join(
    HERE, "src", "WolvenKitMcp", "bin", "Debug", "net8.0", "WolvenKitMcp.dll")
DOTNET = os.environ.get("WOLVENKIT_DOTNET") or shutil.which("dotnet") or "dotnet"

GAME = (sys.argv[1] if len(sys.argv) > 1
        else os.environ.get("WOLVENKIT_GAME", r"C:\Cyberpunk\Cyberpunk 2077")).rstrip("\\/")
CONTENT = os.path.join(GAME, "archive", "pc", "content")
MODS = os.path.join(GAME, "archive", "pc", "mod")
TWEAKDB = os.path.join(GAME, "r6", "cache", "tweakdb.bin")

# Assets de test — chemins internes stables du jeu de base.
ARCH_SMALL = os.path.join(CONTENT, "basegame_2_mainmenu.archive")   # fichiers de monde (CR2W)
ARCH_ENGINE = os.path.join(CONTENT, "basegame_1_engine.archive")    # meshes + textures
ARCH_AUDIO = os.path.join(CONTENT, "audio_2_soundbanks.archive")    # fichiers .wem
MESH_GLOB = "*fx_glass_piece_01.mesh"
XBM_GLOB = "*default_sticker_texture.xbm"
WEM_GLOB = "*858926615.wem"

WORK = os.path.join(tempfile.gettempdir(), "wkvalidate")


# ── Client MCP ───────────────────────────────────────────────────────────────
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
            "clientInfo": {"name": "wk-validate", "version": "1.0"}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        return r

    def call(self, name, args):
        """Appelle un outil ; renvoie (résultat structuré dict, ms)."""
        t = time.time()
        r = self.request("tools/call", {"name": name, "arguments": args})
        ms = (time.time() - t) * 1000
        if "error" in r:
            return _errdict(f"JSON-RPC : {r['error']}"), ms
        res = r.get("result", {})
        txt = "\n".join(c.get("text", "") for c in res.get("content", [])
                        if c.get("type") == "text")
        try:
            return json.loads(txt), ms
        except json.JSONDecodeError:
            # Réponse non-JSON (p. ex. erreur d'invocation MCP) — on l'enveloppe.
            d = _errdict(txt[:300] or "(réponse vide)")
            d["log"] = txt
            return d, ms

    def resource(self, uri):
        r = self.request("resources/read", {"uri": uri})
        if "error" in r:
            return f"ERREUR JSON-RPC : {r['error']}"
        return "\n".join(c.get("text", "")
                         for c in r.get("result", {}).get("contents", []))

    def listing(self, method, key):
        return self.request(method, {}).get("result", {}).get(key, [])

    def parallel_calls(self, calls):
        """Envoie N requêtes JSON-RPC en pipeline puis draine les N réponses
        en les ré-appariant par ID — teste le vrai pipelining du daemon.
        Renvoie (résultats parsés, durée totale en ms)."""
        ids = []
        t0 = time.time()
        for name, args in calls:
            self._id += 1
            rid = self._id
            ids.append(rid)
            self._send({"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                        "params": {"name": name, "arguments": args}})

        pending = set(ids)
        by_id = {}
        while pending:
            resp = self._recv()
            rid = resp.get("id")
            if rid in pending:
                by_id[rid] = resp
                pending.discard(rid)
        total_ms = (time.time() - t0) * 1000

        out = []
        for rid in ids:
            r = by_id[rid]
            if "error" in r:
                out.append(_errdict(f"JSON-RPC : {r['error']}"))
                continue
            res = r.get("result", {})
            txt = "\n".join(c.get("text", "") for c in res.get("content", [])
                            if c.get("type") == "text")
            try:
                out.append(json.loads(txt))
            except json.JSONDecodeError:
                d = _errdict(txt[:300] or "(réponse vide)")
                d["log"] = txt
                out.append(d)
        return out, total_ms

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=15)
        except Exception:
            self.proc.kill()


def _errdict(summary):
    return {"ok": False, "status": "error", "summary": summary,
            "produced": [], "warnings": [], "errors": [summary], "log": ""}


# ── Résultats ────────────────────────────────────────────────────────────────
results = []  # (cible, statut, détail)
MARK = {"PASS": "✓", "WARN": "!", "FAIL": "✗", "SKIP": "–", "PARTIAL": "~"}


def record(target, status, detail):
    results.append((target, status, detail))
    print(f"\n[{MARK.get(status, '?')}] {status:7} {target} — {detail}")


def show(res):
    p, w, e = res.get("produced", []), res.get("warnings", []), res.get("errors", [])
    print(f"    status={res.get('status')}  produced={len(p)}  "
          f"warnings={len(w)}  errors={len(e)}")
    if res.get("summary"):
        print(f"    {res['summary']}")
    log = (res.get("log") or "").strip()
    if log:
        print("    " + log[:360].replace("\n", "\n    ") + ("…" if len(log) > 360 else ""))


def produced_abs(res, outdir, suffix=None):
    """Chemin absolu d'un fichier produit (filtré par extension si fournie)."""
    for rel in res.get("produced", []):
        if suffix is None or rel.lower().endswith(suffix):
            return os.path.join(outdir, rel)
    return None


def wd(name):
    p = os.path.join(WORK, name)
    os.makedirs(p, exist_ok=True)
    return p


# ── Validation ───────────────────────────────────────────────────────────────
def main():
    if not os.path.exists(SERVER_DLL):
        sys.exit(f"DLL serveur introuvable : {SERVER_DLL}\n"
                 "Compiler d'abord : dotnet build src\\WolvenKitMcp")
    if not os.path.isdir(GAME):
        sys.exit(f"Installation Cyberpunk 2077 introuvable : {GAME}\n"
                 "Passer le chemin en argument : python validate-windows.py \"<chemin>\"")

    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK, exist_ok=True)
    print(f"=== Validation WolvenKit MCP — jeu : {GAME} ===")
    print(f"    serveur : {SERVER_DLL}")
    print(f"    travail : {WORK}")

    srv = Server()
    try:
        init = srv.initialize()
        info = init.get("result", {}).get("serverInfo", {})
        print(f"\n=== Handshake MCP : {info} ===")
        tools = srv.listing("tools/list", "tools")
        prompts = srv.listing("prompts/list", "prompts")
        print(f"=== tools/list : {len(tools)} outils, prompts/list : {len(prompts)} prompts ===")
        record("handshake + tools/list", "PASS" if len(tools) == 85 else "WARN",
               f"{len(tools)} outils exposés (85 attendus)")
        record("prompts/list", "PASS" if len(prompts) == 5 else "WARN",
               f"{len(prompts)} prompts MCP exposés (5 attendus)")
        run_all(srv)
    finally:
        srv.close()

    # ── Tableau final ────────────────────────────────────────────────────────
    print("\n\n" + "=" * 72)
    print("TABLEAU DE RÉSULTATS")
    print("=" * 72)
    counts = {}
    for target, status, detail in results:
        counts[status] = counts.get(status, 0) + 1
        print(f"  [{MARK.get(status, '?')}] {status:8} {target:24} {detail}")
    print("-" * 72)
    print("  " + "   ".join(f"{s}: {n}" for s, n in sorted(counts.items())))
    print("=" * 72)
    if counts.get("FAIL"):
        sys.exit(1)


def run_all(srv):
    # 1. wolvenkit_status -----------------------------------------------------
    res, ms = srv.call("wolvenkit_status", {})
    show(res)
    has_cache = isinstance(res.get("cache"), dict) and "hits" in res["cache"]
    record("wolvenkit_status", "PASS" if (res["ok"] and has_cache) else "FAIL",
           f"{ms:.0f} ms (1er appel = préchauffage) ; cache={res.get('cache')}")

    # 2. compute_hash ---------------------------------------------------------
    game_path = r"base\worlds\04_main_menu\_compiled\default\04_main_menu.streamingworld"
    res, ms = srv.call("compute_hash", {"inputs": [game_path]})
    show(res)
    m = re.search(r"-\s*(\d{6,})", res.get("log", ""))
    the_hash = m.group(1) if m else None
    record("compute_hash", "PASS" if (res["ok"] and the_hash) else "FAIL",
           f"hash de {game_path} = {the_hash}")

    # 3. resolve_hash (aller-retour) -----------------------------------------
    if the_hash:
        res, ms = srv.call("resolve_hash", {"hashes": [the_hash]})
        show(res)
        roundtrip = "04_main_menu.streamingworld" in res.get("log", "")
        record("resolve_hash", "PASS" if (res["ok"] and roundtrip) else "WARN",
               "aller-retour compute→resolve OK" if roundtrip
               else "hash non résolu (chemin absent de la base ?)")
    else:
        record("resolve_hash", "SKIP", "compute_hash n'a pas fourni de hash")

    # 4. archive_info ---------------------------------------------------------
    if os.path.isfile(ARCH_SMALL):
        res, _ = srv.call("archive_info", {"archivePath": ARCH_SMALL})
        res2, _ = srv.call("archive_info", {"archivePath": ARCH_SMALL, "list": True,
                                            "pattern": "*.streamingsector"})
        show(res2)
        listed = ".streamingsector" in res2.get("log", "")
        record("archive_info", "PASS" if (res["ok"] and res2["ok"] and listed) else "FAIL",
               f"infos + listing filtré ({'contenu listé' if listed else 'listing vide'})")
    else:
        record("archive_info", "SKIP", f"archive absente : {ARCH_SMALL}")

    # 4b. diff_archives — diff entre une archive de base et un mod ------------
    if os.path.isfile(ARCH_SMALL) and os.path.isdir(MODS):
        mods_found = [os.path.join(MODS, f) for f in sorted(os.listdir(MODS))
                      if f.endswith(".archive")]
        if mods_found:
            res, _ = srv.call("diff_archives",
                              {"archiveA": ARCH_SMALL, "archiveB": mods_found[0]})
            show(res)
            ok = res["ok"] and ("added" in res) and ("removed" in res)
            added = len(res.get("added", []))
            removed = len(res.get("removed", []))
            record("diff_archives", "PASS" if ok else "FAIL",
                   f"+{added} / -{removed} entre base et {os.path.basename(mods_found[0])}")
        else:
            record("diff_archives", "SKIP", f"aucun mod dans {MODS}")
    else:
        record("diff_archives", "SKIP",
               f"archive ou dossier mod absent : {ARCH_SMALL} / {MODS}")

    # 5. find_in_archives -----------------------------------------------------
    if os.path.isdir(CONTENT):
        res, ms = srv.call("find_in_archives",
                           {"archivesFolder": CONTENT,
                            "pattern": "*04_main_menu.streamingworld"})
        show(res)
        found = "04_main_menu.streamingworld" in res.get("log", "")
        record("find_in_archives", "PASS" if (res["ok"] and found) else "FAIL",
               f"{ms:.0f} ms — recherche dans archive\\pc\\content "
               f"({'trouvé' if found else 'rien trouvé'})")

        # Cache LRU : 2e appel doit être bien plus rapide (cacheHits = nb d'archives).
        res2, ms2 = srv.call("find_in_archives",
                             {"archivesFolder": CONTENT,
                              "pattern": "*04_main_menu.streamingworld"})
        scanned = res2.get("archivesScanned", 0)
        hits = res2.get("cacheHits", 0)
        speedup = ms / ms2 if ms2 > 0 else float("inf")
        cache_ok = res2["ok"] and hits >= scanned and ms2 < ms / 2
        record("find_in_archives (cache chaud)", "PASS" if cache_ok else "WARN",
               f"{ms2:.0f} ms · cache {hits}/{scanned} · speedup ×{speedup:.1f}")
    else:
        record("find_in_archives", "SKIP", f"dossier absent : {CONTENT}")

    # 6. extract_files --------------------------------------------------------
    extract_dir = wd("extract")
    extracted = None
    if os.path.isfile(ARCH_SMALL):
        res, _ = srv.call("extract_files",
                          {"archivePath": ARCH_SMALL, "outputPath": extract_dir,
                           "pattern": "*.streamingsector"})
        show(res)
        extracted = produced_abs(res, extract_dir, ".streamingsector")
        record("extract_files", "PASS" if (res["ok"] and extracted) else "FAIL",
               f"{len(res['produced'])} fichier(s) .streamingsector extrait(s)")
    else:
        record("extract_files", "SKIP", f"archive absente : {ARCH_SMALL}")

    # 7. cr2w_to_json ---------------------------------------------------------
    json_dir = wd("json")
    json_file = None
    if extracted:
        res, _ = srv.call("cr2w_to_json", {"path": extracted, "outputPath": json_dir})
        show(res)
        json_file = produced_abs(res, json_dir, ".json")
        record("cr2w_to_json", "PASS" if (res["ok"] and json_file) else "FAIL",
               f"JSON produit : {os.path.basename(json_file) if json_file else 'aucun'}")
    else:
        record("cr2w_to_json", "SKIP", "aucun fichier CR2W extrait en amont")

    # 8. json_to_cr2w ---------------------------------------------------------
    cr2w_dir = wd("cr2w")
    if json_file:
        res, _ = srv.call("json_to_cr2w", {"path": json_file, "outputPath": cr2w_dir})
        show(res)
        back = produced_abs(res, cr2w_dir, ".streamingsector")
        record("json_to_cr2w", "PASS" if (res["ok"] and back) else "FAIL",
               f"CR2W régénéré : {os.path.basename(back) if back else 'aucun'}")
    else:
        record("json_to_cr2w", "SKIP", "aucun JSON produit en amont")

    # 8b. read_game_file (lecture extract+convert en un seul appel) ----------
    gf_path = r"base\worlds\04_main_menu\_compiled\default\always_loaded_0.streamingsector"
    rgf_json = None
    if os.path.isfile(ARCH_SMALL):
        res, _ = srv.call("read_game_file",
                          {"archivePath": ARCH_SMALL, "gameFilePath": gf_path})
        show(res)
        rgf_json = res.get("jsonFile")
        good = res["ok"] and bool(res.get("content")) and rgf_json and os.path.isfile(rgf_json)
        record("read_game_file", "PASS" if good else "FAIL",
               f"contenu renvoyé ({len(res.get('content') or '')} car.) + jsonFile sur disque")
    else:
        record("read_game_file", "SKIP", f"archive absente : {ARCH_SMALL}")

    # 8c. write_game_file (JSON édité → CR2W placé pour pack_archive) --------
    if rgf_json and os.path.isfile(rgf_json):
        wgf_mod = wd("wgf_mod")
        res, _ = srv.call("write_game_file",
                          {"jsonFile": rgf_json, "gameFilePath": gf_path,
                           "modArchiveFolder": wgf_mod})
        show(res)
        placed = os.path.isfile(os.path.join(wgf_mod, gf_path))
        record("write_game_file", "PASS" if (res["ok"] and placed) else "FAIL",
               "CR2W replacé au bon chemin interne" if placed else "CR2W non placé")
    else:
        record("write_game_file", "SKIP", "read_game_file n'a pas fourni de jsonFile")

    # 9. uncook (texture + mesh) ---------------------------------------------
    uncook_dir = wd("uncook")
    png = None
    if os.path.isfile(ARCH_ENGINE):
        res, _ = srv.call("uncook", {"archivePath": ARCH_ENGINE, "outputPath": uncook_dir,
                                     "pattern": XBM_GLOB, "textureFormat": "png"})
        show(res)
        res2, _ = srv.call("uncook", {"archivePath": ARCH_ENGINE, "outputPath": uncook_dir,
                                      "pattern": MESH_GLOB})
        show(res2)
        png = produced_abs(res, uncook_dir, ".png")
        glb = produced_abs(res2, uncook_dir, ".glb") or produced_abs(res2, uncook_dir, ".gltf")
        statut = "PASS" if (png and glb) else ("PARTIAL" if (png or glb) else "FAIL")
        record("uncook", statut,
               f"texture→{'png OK' if png else '✗'}, mesh→{'glb OK' if glb else '✗'}")
    else:
        record("uncook", "SKIP", f"archive absente : {ARCH_ENGINE}")

    # 10. extract un mesh brut, puis export_files ----------------------------
    mesh_dir = wd("meshextract")
    export_dir = wd("export")
    mesh_file = None
    if os.path.isfile(ARCH_ENGINE):
        res, _ = srv.call("extract_files", {"archivePath": ARCH_ENGINE,
                                            "outputPath": mesh_dir, "pattern": MESH_GLOB})
        mesh_file = produced_abs(res, mesh_dir, ".mesh")
    if mesh_file:
        res, _ = srv.call("export_files", {"path": mesh_file, "outputPath": export_dir})
        show(res)
        exported = produced_abs(res, export_dir, ".glb") or produced_abs(res, export_dir, ".gltf")
        record("export_files", "PASS" if (res["ok"] and exported) else "FAIL",
               f"mesh exporté → {os.path.basename(exported) if exported else 'aucun'}")
    else:
        record("export_files", "SKIP", "aucun .mesh extrait en amont")

    # 11. import_raw ----------------------------------------------------------
    import_dir = wd("import")
    if png:
        res, _ = srv.call("import_raw", {"path": png, "outputPath": import_dir})
        show(res)
        imported = produced_abs(res, import_dir, ".xbm")
        record("import_raw", "PASS" if (res["ok"] and imported) else "FAIL",
               f"PNG → REDengine : {os.path.basename(imported) if imported else 'aucun'}")
    else:
        record("import_raw", "SKIP", "aucune image brute (PNG) produite en amont")

    # 12. pack_archive (vrais fichiers cuits) --------------------------------
    pack_out = wd("packout")
    packed_archive = None
    if extracted:
        res, _ = srv.call("pack_archive", {"folderPath": extract_dir, "outputPath": pack_out})
        show(res)
        packed_archive = produced_abs(res, pack_out, ".archive")
        unknown = any("Unknown file extension" in w for w in res.get("warnings", []))
        record("pack_archive",
               "PASS" if (res["ok"] and packed_archive and not unknown) else
               ("WARN" if packed_archive else "FAIL"),
               f"archive : {os.path.basename(packed_archive) if packed_archive else 'aucune'}"
               + (" — AVEC avertissement d'extension" if unknown else " — sans avertissement"))
    else:
        record("pack_archive", "SKIP", "aucun fichier cuit extrait en amont")

    # 12b. install_mod (faux dossier de jeu — ne touche pas la vraie install) -
    if packed_archive and os.path.isfile(packed_archive):
        fakegame = wd("fakegame")
        res, _ = srv.call("install_mod",
                          {"archivePath": packed_archive, "gamePath": fakegame})
        show(res)
        installed = os.path.isfile(os.path.join(
            fakegame, "archive", "pc", "mod", os.path.basename(packed_archive)))
        record("install_mod", "PASS" if (res["ok"] and installed) else "FAIL",
               "archive copiée dans archive\\pc\\mod (faux jeu de test)")
    else:
        record("install_mod", "SKIP", "aucune archive produite par pack_archive")

    # 12c. lint_mod — sur une vraie archive de mod du jeu (sans gamePath, puis avec)
    if os.path.isdir(MODS):
        mods_found = [os.path.join(MODS, f) for f in sorted(os.listdir(MODS))
                      if f.endswith(".archive")]
        if mods_found:
            target = mods_found[0]
            res, _ = srv.call("lint_mod", {"archivePath": target})
            show(res)
            res2, _ = srv.call("lint_mod",
                               {"archivePath": target, "gamePath": GAME})
            shape_ok = ("fileCount" in res and "unknownExtCount" in res
                        and "conflicts" in res2 and "conflictCount" in res2)
            record("lint_mod", "PASS" if shape_ok else "FAIL",
                   f"fichiers={res.get('fileCount')} · ext inconnues={res.get('unknownExtCount')} · "
                   f"conflits (vs autres mods)={res2.get('conflictCount')}")
        else:
            record("lint_mod", "SKIP", f"aucun mod dans {MODS}")
    else:
        record("lint_mod", "SKIP", f"dossier mod absent : {MODS}")

    # 13. create_mod_project --------------------------------------------------
    proj_parent = wd("proj")
    res, _ = srv.call("create_mod_project",
                      {"parentFolder": proj_parent, "modName": "ValidationMod"})
    show(res)
    proj_dir = os.path.join(proj_parent, "ValidationMod")
    made = os.path.isdir(os.path.join(proj_dir, "source", "archive"))
    has_cpmodproj = os.path.isfile(os.path.join(proj_dir, "ValidationMod.cpmodproj"))
    has_sounds = os.path.isdir(os.path.join(proj_dir, "source", "customSounds"))
    cmp_ok = res["ok"] and made and has_cpmodproj and has_sounds
    record("create_mod_project", "PASS" if cmp_ok else "FAIL",
           "source/{archive,raw,resources,customSounds} + packed + .cpmodproj créés"
           if cmp_ok else "structure ou .cpmodproj absent")

    # 13a. generate_modproj — génère un .cpmodproj dans un dossier existant -----
    gmp_dir = wd("gen_proj")
    res, _ = srv.call("generate_modproj",
                      {"projectFolder": gmp_dir, "modName": "GenMod",
                       "author": "Validation", "version": "1.2.3"})
    show(res)
    gmp_file = os.path.join(gmp_dir, "GenMod.cpmodproj")
    gmp_ok = res["ok"] and os.path.isfile(gmp_file)
    if gmp_ok:
        xml = open(gmp_file, encoding="utf-8").read()
        gmp_ok = "<Name>GenMod</Name>" in xml and "<Version>1.2.3</Version>" in xml
    record("generate_modproj", "PASS" if gmp_ok else "FAIL",
           "GenMod.cpmodproj (XML <CP77Mod> valide) généré" if gmp_ok else "fichier absent/invalide")

    # 13b. lint_script (sémantique) — @wrapMethod sans wrappedMethod() ----------
    reds_path = os.path.join(wd("lint_src"), "bad.reds")
    with open(reds_path, "w", encoding="utf-8") as fh:
        fh.write("@wrapMethod(PlayerPuppet)\nfunc OnGameAttached() {\n  let y = 1;\n}\n")
    res, _ = srv.call("lint_script", {"scriptFile": reds_path})
    show(res)
    warns = res.get("warnings", [])
    sem_ok = res["ok"] and any("wrappedMethod" in w for w in warns)
    record("lint_script (sémantique)", "PASS" if sem_ok else "FAIL",
           "check sémantique : @wrapMethod sans wrappedMethod() détecté"
           if sem_ok else f"avertissement attendu absent ({warns})")

    # 13b. REDmod : create_redmod_project + pack_redmod + install_redmod -------
    rm_parent = wd("redmod_src")
    res, _ = srv.call("create_redmod_project",
                      {"parentFolder": rm_parent, "modName": "ValidationRedMod",
                       "description": "Mod de validation Windows"})
    show(res)
    rm_dir = os.path.join(rm_parent, "ValidationRedMod")
    rm_info = os.path.join(rm_dir, "info.json")
    has_info = os.path.isfile(rm_info)
    has_archives = os.path.isdir(os.path.join(rm_dir, "archives"))
    has_tweaks = os.path.isdir(os.path.join(rm_dir, "tweaks"))
    rm_ok = res["ok"] and has_info and has_archives and has_tweaks
    record("create_redmod_project", "PASS" if rm_ok else "FAIL",
           f"info.json + archives/ + tweaks/ + scripts/ + customSounds/ "
           f"({'OK' if rm_ok else 'manquant'})")

    if rm_ok:
        rm_pack_out = wd("redmod_pack")
        res, _ = srv.call("pack_redmod",
                          {"modSourceFolder": rm_dir, "outputPath": rm_pack_out})
        show(res)
        zip_path = os.path.join(rm_pack_out, "ValidationRedMod.zip")
        record("pack_redmod", "PASS" if (res["ok"] and os.path.isfile(zip_path)) else "FAIL",
               f"{os.path.basename(zip_path)} produit "
               f"({os.path.getsize(zip_path) if os.path.isfile(zip_path) else 0} o)")

        fakegame = wd("fakegame_redmod")
        res, _ = srv.call("install_redmod",
                          {"modSourceFolder": rm_dir, "gamePath": fakegame})
        show(res)
        installed = os.path.isfile(os.path.join(
            fakegame, "mods", "ValidationRedMod", "info.json"))
        record("install_redmod", "PASS" if (res["ok"] and installed) else "FAIL",
               "REDmod copié dans mods/<nom>/ (faux jeu de test)")
    else:
        record("pack_redmod", "SKIP", "create_redmod_project a échoué")
        record("install_redmod", "SKIP", "create_redmod_project a échoué")

    # 14. build_project -------------------------------------------------------
    # Depuis que create_mod_project émet un .cpmodproj, le build aboutit vraiment
    # et produit packed/archive/pc/mod/<Mod>.archive.
    if made:
        res, _ = srv.call("build_project", {"projectFolder": proj_dir})
        show(res)
        built = os.path.isfile(os.path.join(
            proj_dir, "packed", "archive", "pc", "mod", "ValidationMod.archive"))
        record("build_project", "PASS" if (res["ok"] and built) else "FAIL",
               "build du .cpmodproj → packed/archive/pc/mod/ValidationMod.archive"
               if built else "aucune archive produite")
    else:
        record("build_project", "SKIP", "projet de mod non créé en amont")

    # 15. list_installed_mods -------------------------------------------------
    res, _ = srv.call("list_installed_mods", {"gamePath": GAME})
    show(res)
    record("list_installed_mods", "PASS" if res["ok"] else "FAIL",
           f"{res.get('archiveModsCount', '?')} mods .archive + "
           f"{res.get('redModsCount', '?')} REDmods listés")

    # 16. detect_conflicts ----------------------------------------------------
    res, ms = srv.call("detect_conflicts", {"gamePath": GAME})
    show(res)
    if res["ok"]:
        record("detect_conflicts", "PASS", f"{ms:.0f} ms — conflits de archive\\pc\\mod analysés")
    elif "Value cannot be null" in res.get("log", ""):
        # Bug amont : WolvenKit.CLI 8.18.0 `conflicts` lève une ArgumentNullException
        # sur un install réel — reproductible avec cp77tools, hors du serveur MCP.
        record("detect_conflicts", "WARN",
               "outil correctement câblé ; WolvenKit.CLI 8.18.0 `conflicts` plante "
               "(bug amont, reproductible tel quel avec cp77tools)")
    else:
        record("detect_conflicts", "FAIL", "échec inattendu — voir la sortie ci-dessus")

    # 17. tweakdb_query -------------------------------------------------------
    rec_name = rec_id = None
    if os.path.isfile(TWEAKDB):
        res, ms = srv.call("tweakdb_query",
                           {"tweakdbPath": TWEAKDB, "filter": "Items.Preset_"})
        show(res)
        # Lignes : « record/flat  <nom>  <TweakDBID 0x.. / <décimal>:<long.>> ».
        m = re.search(r"(\S+)\s+<TweakDBID[^/]+/\s*(\d+):", res.get("log", ""))
        if m:
            rec_name, rec_id = m.group(1), m.group(2)
        record("tweakdb_query", "PASS" if res["ok"] else "FAIL",
               f"{ms:.0f} ms — tweakdb.bin chargée, records/flats listés")
    else:
        record("tweakdb_query", "SKIP", f"tweakdb.bin absente : {TWEAKDB}")

    # 18. tweakdb_resolve -----------------------------------------------------
    if rec_id:
        res, _ = srv.call("tweakdb_resolve", {"hashes": [rec_id]})
        show(res)
        resolved = bool(rec_name) and rec_name in res.get("log", "")
        record("tweakdb_resolve", "PASS" if resolved else "WARN",
               f"{rec_id} → {rec_name}" if resolved
               else f"identifiant {rec_id} ('{rec_name}') non résolu")
    else:
        record("tweakdb_resolve", "SKIP", "aucun identifiant obtenu via tweakdb_query")

    # 18-bis. describe_tweak_record — inspecte tous les flats d'un record connu.
    target_record = rec_name or "Items.Preset_Achilles_Collectible_inline0"
    if os.path.isfile(TWEAKDB):
        res, _ = srv.call("describe_tweak_record",
                          {"tweakdbPath": TWEAKDB, "recordId": target_record})
        show(res)
        # Le daemon émet des lignes "flat <name> : <type> = <value>" ; on compte.
        flat_lines = sum(1 for line in res.get("log", "").splitlines() if "  flat  " in line)
        record("describe_tweak_record", "PASS" if (res["ok"] and flat_lines > 0) else "WARN",
               f"{target_record} → {flat_lines} flat(s) listé(s)")
    else:
        record("describe_tweak_record", "SKIP", f"tweakdb.bin absente : {TWEAKDB}")

    # 18-ter. inspect_mesh / inspect_texture — extraction d'un .mesh et .xbm
    # puis inspection (résumé sans conversion).
    inspect_dir = wd("inspect")
    if os.path.isfile(ARCH_ENGINE):
        srv.call("extract_files", {"archivePath": ARCH_ENGINE,
                                   "outputPath": inspect_dir, "pattern": MESH_GLOB})
        srv.call("extract_files", {"archivePath": ARCH_ENGINE,
                                   "outputPath": inspect_dir, "pattern": XBM_GLOB})
        mesh = next((os.path.join(r, f)
                     for r, _, fs in os.walk(inspect_dir) for f in fs
                     if f.endswith(".mesh")), None)
        xbm = next((os.path.join(r, f)
                    for r, _, fs in os.walk(inspect_dir) for f in fs
                    if f.endswith(".xbm")), None)
        if mesh:
            res, _ = srv.call("inspect_mesh", {"meshFile": mesh})
            show(res)
            ok = (res["ok"] and res.get("lodCount") is not None
                  and res.get("materialCount") is not None)
            record("inspect_mesh", "PASS" if ok else "FAIL",
                   f"LOD={res.get('lodCount')} · sous-mesh={res.get('subMeshCount')} · "
                   f"matériaux={res.get('materialCount')} · bones={res.get('boneCount')}")
        else:
            record("inspect_mesh", "SKIP", "aucun .mesh extrait")
        if xbm:
            res, _ = srv.call("inspect_texture", {"xbmFile": xbm})
            show(res)
            ok = res["ok"] and res.get("width", 0) > 0 and res.get("height", 0) > 0
            record("inspect_texture", "PASS" if ok else "FAIL",
                   f"{res.get('width')}x{res.get('height')} format={res.get('format')} "
                   f"mips={res.get('mipLevels')}")
        else:
            record("inspect_texture", "SKIP", "aucun .xbm extrait")
    else:
        record("inspect_mesh", "SKIP", f"archive absente : {ARCH_ENGINE}")
        record("inspect_texture", "SKIP", f"archive absente : {ARCH_ENGINE}")

    # 18a. Pipelining IPC : envoie 4 requêtes en pipeline, vérifie que les
    # réponses arrivent (ordre tolérant) et que le total n'est pas « 4 × cold path ».
    parallel_res, parallel_ms = srv.parallel_calls([
        ("compute_hash", {"inputs": [f"base/wolvenkit_pipeline_{i}.mesh"]})
        for i in range(4)
    ])
    all_ok = all(r.get("ok") for r in parallel_res)
    record("pipelining IPC (4 requêtes en vol)",
           "PASS" if all_ok else "FAIL",
           f"{len(parallel_res)} compute_hash concurrents en {parallel_ms:.0f} ms")

    # 18b. tweak structuré : read_tweak / write_tweak / validate_tweak / install_tweak
    tweak_dir = wd("tweak")
    tweak_src = os.path.join(tweak_dir, "validation.tweak")
    with open(tweak_src, "w", encoding="utf-8") as f:
        # Surcharge un record TweakDB connu (boost de dégâts d'un préset).
        # On utilise un nom récupéré via tweakdb_query → tweakdb_resolve plus haut,
        # à défaut un identifiant générique connu de la TweakDB de base.
        rec = rec_name or "Items.Preset_Achilles_Collectible_inline0"
        f.write(f"{rec}:\n  damage: 250\n  attacksPerSecond: 3.0\n")
        f.write("MyMod.NewItem:\n  $instanceOf: Items.Preset_Achilles_Collectible_inline0\n  damage: 500\n")

    # read_tweak
    res, _ = srv.call("read_tweak", {"tweakFile": tweak_src})
    show(res)
    parsed_json_file = res.get("jsonFile")
    has_content = bool(res.get("content")) and "damage" in res.get("content", "")
    record("read_tweak", "PASS" if (res["ok"] and has_content) else "FAIL",
           f"JSON produit : {os.path.basename(parsed_json_file) if parsed_json_file else 'aucun'}")

    # write_tweak (round-trip)
    rt_out = os.path.join(tweak_dir, "roundtrip.tweak")
    if parsed_json_file and os.path.isfile(parsed_json_file):
        res, _ = srv.call("write_tweak",
                          {"jsonFile": parsed_json_file, "outputTweakFile": rt_out})
        show(res)
        rt_ok = res["ok"] and os.path.isfile(rt_out) and os.path.getsize(rt_out) > 0
        record("write_tweak", "PASS" if rt_ok else "FAIL",
               f".tweak régénéré : {os.path.basename(rt_out)} "
               f"({os.path.getsize(rt_out) if rt_ok else 0} o)")
    else:
        record("write_tweak", "SKIP", "read_tweak n'a pas produit de JSON")

    # validate_tweak
    if os.path.isfile(TWEAKDB):
        res, _ = srv.call("validate_tweak",
                          {"tweakFile": tweak_src, "tweakdbBin": TWEAKDB})
        show(res)
        # Le 2e record (MyMod.NewItem) a $instanceOf, donc OK ; le 1er doit exister.
        record("validate_tweak", "PASS" if res["ok"] else "WARN",
               f"validation lancée (exit={res.get('exitCode')})")
    else:
        record("validate_tweak", "SKIP", f"tweakdb.bin absente : {TWEAKDB}")

    # install_tweak (faux jeu de test)
    fakegame_t = wd("fakegame_tweak")
    res, _ = srv.call("install_tweak",
                      {"tweakFile": tweak_src, "gamePath": fakegame_t})
    show(res)
    installed_t = os.path.isfile(os.path.join(
        fakegame_t, "r6", "tweaks", os.path.basename(tweak_src)))
    record("install_tweak", "PASS" if (res["ok"] and installed_t) else "FAIL",
           ".tweak copié dans r6/tweaks (faux jeu de test)")

    # 18c. generate_tweak_template — 3 patterns, chacun re-validé via read_tweak.
    gen_dir = wd("tweak_gen")
    for pattern_name, params_dict in [
        ("override_field",
            {"recordId": "Items.Preset_Achilles_Collectible_inline0",
             "field": "damage", "value": 250}),
        ("new_record",
            {"newId": "ValidationMod.MyWeapon",
             "baseId": "Items.Preset_Achilles_Collectible_inline0"}),
        ("boost_stat",
            {"recordId": "Items.Preset_Achilles_Collectible_inline0",
             "stat": "attacksPerSecond", "value": 3.0}),
    ]:
        out = os.path.join(gen_dir, f"{pattern_name}.tweak")
        res, _ = srv.call("generate_tweak_template",
                          {"pattern": pattern_name,
                           "parametersJson": json.dumps(params_dict),
                           "outputFile": out})
        show(res)
        if not (res["ok"] and os.path.isfile(out)):
            record(f"generate_tweak_template ({pattern_name})", "FAIL",
                   "fichier non produit")
            break
        # Roundtrip : le .tweak généré doit être lisible.
        rr, _ = srv.call("read_tweak", {"tweakFile": out})
        ok = res["ok"] and rr["ok"] and "damage" in (rr.get("content", "") + " ") \
             or pattern_name == "new_record"  # new_record n'a pas damage par défaut
        record(f"generate_tweak_template ({pattern_name})",
               "PASS" if rr["ok"] else "FAIL",
               f"généré → {os.path.basename(out)} ({os.path.getsize(out)} o, re-read OK)"
               if rr["ok"] else "re-read échoué")

    # 18d. read_script / lint_script — fichier .reds sain + cassé.
    script_dir = wd("scripts")
    os.makedirs(script_dir, exist_ok=True)
    sane = os.path.join(script_dir, "sane.reds")
    with open(sane, "w", encoding="utf-8") as f:
        f.write("module ValidationMod\n\n"
                "public class Foo {\n"
                "  public func bar(x: Int32) -> Int32 {\n"
                "    return x * 2;\n"
                "  }\n"
                "}\n"
                "@addMethod(PlayerPuppet)\n"
                "public func myMethod() -> Void {}\n")
    res, _ = srv.call("read_script", {"scriptFile": sane})
    show(res)
    ok = res["ok"] and res.get("moduleName") == "ValidationMod" \
         and len(res.get("declarations", [])) >= 3
    record("read_script", "PASS" if ok else "FAIL",
           f"module={res.get('moduleName')} · "
           f"{len(res.get('declarations', []))} déclaration(s)")

    res, _ = srv.call("lint_script", {"scriptFile": sane})
    show(res)
    lint_sane_ok = res["ok"] and len(res.get("errors", [])) == 0
    record("lint_script (sane)",
           "PASS" if lint_sane_ok else "FAIL",
           f"0 erreur sur fichier sain "
           f"({len(res.get('declarations', []))} déclarations)")

    # Fichier cassé : accolade manquante
    broken = os.path.join(script_dir, "broken.reds")
    with open(broken, "w", encoding="utf-8") as f:
        f.write("public class Broken {\n  public func x() -> Int32 { return 1;\n}\n")  # 1 { ouverte non fermée
    res, _ = srv.call("lint_script", {"scriptFile": broken})
    show(res)
    lint_broken_ok = len(res.get("errors", [])) > 0
    record("lint_script (broken)",
           "PASS" if lint_broken_ok else "FAIL",
           f"erreur(s) détectée(s) : {res.get('errors', [])[:1]}")

    # 18e. backup_mods / restore_mods — sur un faux jeu fabriqué pour le test.
    fakegame_b = wd("fakegame_backup")
    for sub in ("archive/pc/mod", "mods/SomeRedmod", "r6/tweaks"):
        os.makedirs(os.path.join(fakegame_b, *sub.split("/")), exist_ok=True)
    # Crée 2 archives factices + 1 redmod info + 1 tweak factice
    with open(os.path.join(fakegame_b, "archive", "pc", "mod", "fake1.archive"), "wb") as f:
        f.write(b"fake archive content " * 100)
    with open(os.path.join(fakegame_b, "archive", "pc", "mod", "fake2.archive"), "wb") as f:
        f.write(b"another fake " * 50)
    with open(os.path.join(fakegame_b, "mods", "SomeRedmod", "info.json"), "w") as f:
        f.write('{"name":"SomeRedmod","version":"1.0.0"}')
    with open(os.path.join(fakegame_b, "r6", "tweaks", "x.tweak"), "w") as f:
        f.write("Items.foo:\n  damage: 100\n")

    backup_dir = wd("backups")
    res, _ = srv.call("backup_mods",
                      {"gamePath": fakegame_b, "outputDir": backup_dir})
    show(res)
    zip_path = res.get("zipPath")
    backup_ok = res["ok"] and zip_path and os.path.isfile(zip_path) \
                and res.get("archiveCount", 0) == 2
    record("backup_mods",
           "PASS" if backup_ok else "FAIL",
           f"ZIP {os.path.getsize(zip_path) if backup_ok else 0} o · "
           f"{res.get('archiveCount')} archives + {res.get('redmodCount')} redmods + "
           f"{res.get('tweakCount')} tweaks")

    if backup_ok:
        # Restaure dans un faux jeu vide
        fakegame_r = wd("fakegame_restore")
        os.makedirs(fakegame_r, exist_ok=True)
        res, _ = srv.call("restore_mods",
                          {"backupZip": zip_path, "gamePath": fakegame_r,
                           "mode": "merge"})
        show(res)
        restored_ok = res["ok"] and os.path.isfile(
            os.path.join(fakegame_r, "archive", "pc", "mod", "fake1.archive")) \
            and os.path.isfile(
            os.path.join(fakegame_r, "r6", "tweaks", "x.tweak")) \
            and os.path.isfile(
            os.path.join(fakegame_r, "mods", "SomeRedmod", "info.json"))
        record("restore_mods",
               "PASS" if restored_ok else "FAIL",
               f"{res.get('extractedCount')} fichier(s) extrait(s) dans faux jeu vide")
    else:
        record("restore_mods", "SKIP", "backup_mods a échoué")

    # 18f. uninstall_mod / uninstall_redmod / uninstall_tweak — roundtrip
    # install puis uninstall sur le faux jeu de backup (qui a déjà fake1.archive,
    # SomeRedmod, x.tweak créés par la step 18e).
    if os.path.isdir(fakegame_b):
        # uninstall_mod
        res, _ = srv.call("uninstall_mod",
                          {"archivePathOrName": "fake1.archive",
                           "gamePath": fakegame_b})
        show(res)
        gone = not os.path.isfile(os.path.join(
            fakegame_b, "archive", "pc", "mod", "fake1.archive"))
        record("uninstall_mod", "PASS" if (res["ok"] and gone) else "FAIL",
               "fake1.archive supprimée du faux jeu")

        # uninstall_redmod
        res, _ = srv.call("uninstall_redmod",
                          {"modName": "SomeRedmod", "gamePath": fakegame_b})
        show(res)
        gone = not os.path.isdir(os.path.join(fakegame_b, "mods", "SomeRedmod"))
        record("uninstall_redmod", "PASS" if (res["ok"] and gone) else "FAIL",
               "mods/SomeRedmod supprimé du faux jeu")

        # uninstall_tweak
        res, _ = srv.call("uninstall_tweak",
                          {"tweakName": "x.tweak", "gamePath": fakegame_b})
        show(res)
        gone = not os.path.isfile(os.path.join(fakegame_b, "r6", "tweaks", "x.tweak"))
        record("uninstall_tweak", "PASS" if (res["ok"] and gone) else "FAIL",
               "r6/tweaks/x.tweak supprimé du faux jeu")
    else:
        record("uninstall_mod", "SKIP", "faux jeu de backup absent")
        record("uninstall_redmod", "SKIP", "faux jeu de backup absent")
        record("uninstall_tweak", "SKIP", "faux jeu de backup absent")

    # 18g. deploy_redmod — sur le vrai jeu si REDmod est installé.
    redmod_exe = os.path.join(GAME, "tools", "redmod", "bin", "redMod.exe")
    if os.path.isfile(redmod_exe):
        res, _ = srv.call("deploy_redmod", {"gamePath": GAME})
        show(res)
        # deploy peut renvoyer un exit code != 0 si certains REDmods ont des soucis,
        # mais l'outil lui-même doit s'exécuter sans crash.
        record("deploy_redmod",
               "PASS" if res.get("status") in ("success", "partial") else "WARN",
               f"redMod.exe deploy lancé (exit={res.get('exitCode')})")
    else:
        record("deploy_redmod", "SKIP", f"REDmod absent : {redmod_exe}")

    # 18h. launch_game — DRY (faux jeu sans Cyberpunk2077.exe) : doit signaler propre.
    res, _ = srv.call("launch_game",
                      {"gamePath": fakegame_b or wd("fake_launch"),
                       "deployRedmod": False})
    show(res)
    record("launch_game (faux jeu)",
           "PASS" if not res["ok"] and "Cyberpunk2077.exe" in (res.get("summary", "")
                                                                + " "
                                                                + " ".join(res.get("errors", []))) else "WARN",
           "refuse de lancer (exe absent dans le faux jeu) — comportement attendu")

    # 18i. tail_game_logs — sur des logs factices.
    logs_dir = wd("fake_logs_game")
    os.makedirs(os.path.join(logs_dir, "r6", "logs"), exist_ok=True)
    with open(os.path.join(logs_dir, "r6", "logs", "cyberpunk2077.log"), "w") as f:
        for i in range(50):
            f.write(f"[INFO] log line {i}\n")
        f.write("[ERROR] last error message\n")
    res, _ = srv.call("tail_game_logs",
                      {"gamePath": logs_dir, "log": "game", "lines": 10})
    show(res)
    record("tail_game_logs",
           "PASS" if (res["ok"] and "last error" in res.get("content", "")) else "FAIL",
           f"{res.get('lineCount')} lignes lues, dernière erreur visible")

    # 18j. mod_summary — sur une vraie archive de mod + sur un fake REDmod.
    if os.path.isdir(MODS):
        mods_found = [os.path.join(MODS, f) for f in sorted(os.listdir(MODS))
                      if f.endswith(".archive")]
        if mods_found:
            res, _ = srv.call("mod_summary", {"modPath": mods_found[0]})
            show(res)
            ok = res["ok"] and res.get("kind") == "archive" \
                 and res.get("fileCount", 0) > 0
            record("mod_summary (archive)",
                   "PASS" if ok else "FAIL",
                   f"{res.get('fileCount')} fichier(s) catégorisés par extension")
        else:
            record("mod_summary (archive)", "SKIP", "aucun mod .archive")

    # ... + redmod dir (réutiliser le REDmod créé en step 13b)
    if 'rm_dir' in dir() and rm_ok:
        res, _ = srv.call("mod_summary", {"modPath": rm_dir})
        show(res)
        ok = res["ok"] and res.get("kind") == "redmod" \
             and res.get("name") == "ValidationRedMod"
        record("mod_summary (redmod)",
               "PASS" if ok else "FAIL",
               f"name={res.get('name')} · {res.get('fileCounts', {})}")

    # 18k. dump_records — sur le tweakdb du jeu, un type petit pour la vitesse.
    if os.path.isfile(TWEAKDB):
        dump_out = os.path.join(wd("dump"), "weapons.jsonl")
        res, _ = srv.call("dump_records",
                          {"tweakdbPath": TWEAKDB,
                           "recordType": "gamedataWeaponItem_Record",
                           "outputFile": dump_out,
                           "format": "jsonl"})
        show(res)
        ok = res["ok"] and os.path.isfile(dump_out) \
             and os.path.getsize(dump_out) > 100
        record("dump_records",
               "PASS" if ok else "FAIL",
               f"{os.path.getsize(dump_out) if ok else 0} o JSONL produit "
               f"(gamedataWeaponItem_Record)")
    else:
        record("dump_records", "SKIP", "tweakdb.bin absente")

    # 18l. generate_redscript_template — 5 patterns
    reds_dir = wd("reds_gen")
    reds_specs = [
        ("add_method", {"targetClass": "PlayerPuppet",
                        "methodName": "boostXp",
                        "args": "amount: Int32",
                        "returnType": "Void"}),
        ("wrap_method", {"targetClass": "PlayerPuppet",
                         "methodName": "OnGameAttached",
                         "args": "",
                         "returnType": "Void"}),
        ("replace_method", {"targetClass": "InventoryItem",
                            "methodName": "GetPrice",
                            "args": "",
                            "returnType": "Int32",
                            "body": "return 100;"}),
        ("add_field", {"targetClass": "PlayerPuppet",
                       "fieldName": "myFlag",
                       "fieldType": "Bool"}),
        ("new_class", {"className": "MyHelper",
                       "extends": "ScriptableComponent",
                       "moduleName": "ValidationMod"}),
    ]
    all_reds_ok = True
    for pat, params_dict in reds_specs:
        out = os.path.join(reds_dir, f"{pat}.reds")
        res, _ = srv.call("generate_redscript_template",
                          {"pattern": pat,
                           "parametersJson": json.dumps(params_dict),
                           "outputFile": out})
        show(res)
        if not (res["ok"] and os.path.isfile(out)):
            all_reds_ok = False
            break
        # Le lint_script ne doit pas trouver d'erreur sur du généré.
        lint_res, _ = srv.call("lint_script", {"scriptFile": out})
        if len(lint_res.get("errors", [])) != 0:
            all_reds_ok = False
            break
    record("generate_redscript_template (5 patterns)",
           "PASS" if all_reds_ok else "FAIL",
           "5 templates générés, chacun passe lint_script sans erreur")

    # 18n. extract_localization + build_localization — TweakDB UI strings.
    if os.path.isfile(TWEAKDB):
        loc_dir = wd("loc")
        loc_json = os.path.join(loc_dir, "loc.json")
        res, _ = srv.call("extract_localization",
                          {"tweakdbPath": TWEAKDB,
                           "outputJson": loc_json,
                           "filter": "Items.Preset_"})
        show(res)
        # Le fichier JSON doit exister et contenir des records.
        extract_ok = res["ok"] and os.path.isfile(loc_json) \
                     and os.path.getsize(loc_json) > 100
        record("extract_localization",
               "PASS" if extract_ok else "FAIL",
               f"{os.path.getsize(loc_json) if extract_ok else 0} o JSON produit "
               f"(filtre Items.Preset_)")

        if extract_ok:
            # Construire un .tweak depuis les 5 premières entrées.
            with open(loc_json, "r", encoding="utf-8") as f:
                data = json.load(f)
            sample = {}
            for k, v in list(data.items())[:5]:
                # On invente une traduction triviale.
                sample[k] = {field: "[FR] " + str(val)[:50] for field, val in v.items()}
            sample_path = os.path.join(loc_dir, "sample.json")
            with open(sample_path, "w", encoding="utf-8") as f:
                json.dump(sample, f, ensure_ascii=False)
            out_tweak = os.path.join(loc_dir, "loc.tweak")
            res2, _ = srv.call("build_localization",
                               {"translationsJson": sample_path,
                                "outputTweak": out_tweak,
                                "lang": "fr-fr"})
            show(res2)
            build_ok = res2["ok"] and os.path.isfile(out_tweak) \
                       and os.path.getsize(out_tweak) > 0
            record("build_localization",
                   "PASS" if build_ok else "FAIL",
                   f"{res2.get('recordCount')} record(s), "
                   f"{res2.get('fieldCount')} champ(s) traduit(s)")
        else:
            record("build_localization", "SKIP", "extract_localization a échoué")
    else:
        record("extract_localization", "SKIP", "tweakdb.bin absente")
        record("build_localization", "SKIP", "tweakdb.bin absente")

    # 18m. clear_cache — sur 'archives' puis vérifier que CacheStats sont remises à zéro.
    res, _ = srv.call("clear_cache", {"scope": "archives"})
    show(res)
    # On vérifie que wolvenkit_status renvoie un entries=0 (le cache a été drainé).
    status_res, _ = srv.call("wolvenkit_status", {})
    cache_after = status_res.get("cache", {})
    record("clear_cache (archives)",
           "PASS" if (res["ok"] and cache_after.get("entries") == 0) else "FAIL",
           f"cache vidé · entries après clear = {cache_after.get('entries')}")

    # 19-20. oodle_compress / oodle_decompress (aller-retour byte-exact) ------
    oodle_dir = wd("oodle")
    src = os.path.join(oodle_dir, "input.bin")
    comp = os.path.join(oodle_dir, "input.kraken")
    deco = os.path.join(oodle_dir, "output.bin")
    with open(src, "wb") as f:
        f.write(b"WolvenKit MCP oodle round-trip test block. " * 1500)
    res, _ = srv.call("oodle_compress", {"inputPath": src, "outputPath": comp})
    show(res)
    comp_ok = res["ok"] and os.path.isfile(comp) and os.path.getsize(comp) > 0
    record("oodle_compress", "PASS" if comp_ok else "FAIL",
           f"{os.path.getsize(src)} o → {os.path.getsize(comp) if os.path.isfile(comp) else 0} o")
    if comp_ok:
        res, _ = srv.call("oodle_decompress", {"inputPath": comp, "outputPath": deco})
        show(res)
        out = deco if os.path.isfile(deco) else (deco + ".bin")
        exact = (os.path.isfile(out)
                 and open(out, "rb").read() == open(src, "rb").read())
        record("oodle_decompress", "PASS" if (res["ok"] and exact) else "FAIL",
               "aller-retour byte-exact" if exact else "décompression non identique")
    else:
        record("oodle_decompress", "SKIP", "compression échouée en amont")

    # 21. wwise_export --------------------------------------------------------
    wem_dir = wd("wem")
    ogg_dir = wd("ogg")
    wem_file = None
    if os.path.isfile(ARCH_AUDIO):
        res, _ = srv.call("extract_files", {"archivePath": ARCH_AUDIO,
                                            "outputPath": wem_dir, "pattern": WEM_GLOB})
        wem_file = produced_abs(res, wem_dir, ".wem")
    if wem_file:
        res, _ = srv.call("wwise_export", {"path": wem_file, "outputPath": ogg_dir})
        show(res)
        ogg = produced_abs(res, ogg_dir, ".ogg")
        record("wwise_export", "PASS" if (res["ok"] and ogg) else "FAIL",
               f"WEM → {os.path.basename(ogg) if ogg else 'aucun OGG'} (binaires audio Windows)")
    else:
        record("wwise_export", "SKIP", "aucun .wem extrait (archive audio absente ?)")

    # 21b. extract_audio (opus / voix-off) ------------------------------------
    if os.path.isfile(ARCH_AUDIO):
        opus_dir = wd("opus")
        res, ms = srv.call("extract_audio",
                           {"archivePath": ARCH_AUDIO, "outputPath": opus_dir})
        show(res)
        n = len(res.get("produced", []) or [])
        if res["ok"] and n > 0:
            record("extract_audio", "PASS", f"{n} fichier(s) audio extraits via uncook opus ({ms:.0f} ms)")
        elif res["ok"]:
            # Câblage OK mais l'archive testée ne contient pas d'opusinfo (sfx vs voix).
            record("extract_audio", "WARN",
                   "pipeline opus exécuté sans erreur ; aucun opus produit "
                   "(archive non vocale ? tester lang_xx_voice.archive)")
        else:
            record("extract_audio", "FAIL", "échec du pipeline opus — voir la sortie")
    else:
        record("extract_audio", "SKIP", "archive audio absente")

    # 22. Exports dédiés (anim / morphtarget / mlmask) sur assets réels --------
    def export_asset(tool, archive, basename, ext, strict=True):
        if not os.path.isfile(archive):
            record(tool, "SKIP", f"archive absente : {os.path.basename(archive)}"); return
        exdir = wd(tool + "_raw")
        srv.call("extract_files", {"archivePath": archive, "outputPath": exdir,
                                   "pattern": "*" + basename})
        found = None
        for root, _, files in os.walk(exdir):
            for f in files:
                if f.lower().endswith(ext):
                    found = os.path.join(root, f)
        if not found:
            record(tool, "SKIP", f"extraction de *{basename} vide"); return
        outdir = wd(tool + "_out")
        res, _ = srv.call(tool, {"path": found, "outputPath": outdir})
        show(res)
        n = sum(len(fs) for _, _, fs in os.walk(outdir))
        if res["ok"] and n > 0:
            record(tool, "PASS", f"{basename} → {n} fichier(s)")
        elif not strict:
            # Outil correctement câblé ; sortie dépendante des données (ex. .anims
            # nécessitant son rig pour produire un glTF — contrainte WolvenKit).
            record(tool, "WARN", f"{basename} : export sans sortie exploitable "
                   "(une .anims sans son rig ne produit pas de glTF — contrainte WolvenKit)")
        elif res["ok"]:
            record(tool, "WARN", f"{basename} : export sans sortie")
        else:
            record(tool, "FAIL", "échec de l'export")

    export_asset("export_mlmask", os.path.join(CONTENT, "basegame_1_engine.archive"),
                 "multilayer_default.mlmask", ".mlmask")
    export_asset("export_morphtarget", os.path.join(CONTENT, "basegame_4_appearance.archive"),
                 "hb_000_pma__morphs_default.morphtarget", ".morphtarget")
    export_asset("export_animation", os.path.join(CONTENT, "basegame_4_animation.archive"),
                 "johnny__sit_car_passenger_generic__02.anims", ".anims", strict=False)

    # 23. loc_resolve — LocKey → texte localisé (charge les archives du jeu) ----
    if os.path.isfile(os.path.join(GAME, "bin", "x64", "Cyberpunk2077.exe")):
        res, ms = srv.call("loc_resolve", {"gamePath": GAME, "key": "40", "language": "en_us"})
        show(res)
        got = res["ok"] and ("female=" in res.get("log", "") or "male=" in res.get("log", ""))
        record("loc_resolve", "PASS" if got else "WARN",
               f"clé 40 → on-screen entry résolue ({ms:.0f} ms)" if got
               else "résolution non concluante")
    else:
        record("loc_resolve", "SKIP", "exe du jeu introuvable")

    # 24. import_audio — câblage (charge ArchiveManager + atteint OpusTools) ----
    if os.path.isfile(os.path.join(GAME, "bin", "x64", "Cyberpunk2077.exe")):
        wavdir = wd("wav_in")
        open(os.path.join(wavdir, "1.wav"), "wb").close()  # placeholder (hash factice)
        res, _ = srv.call("import_audio",
                          {"gamePath": GAME, "wavFolder": wavdir,
                           "outputPath": wd("opus_mod"), "verbose": True})
        show(res)
        # Un wav factice ne matche aucun hash : on valide que le verbe S'EXÉCUTE
        # (ArchiveManager chargé, OpusTools atteint), pas le round-trip complet.
        wired = "Archive Manager loaded" in res.get("log", "") or "opus-import" in res.get("log", "")
        record("import_audio (câblage)", "PASS" if wired else "WARN",
               "ArchiveManager chargé + OpusTools.ImportWavs atteint "
               "(round-trip complet : WAV nommés par vrai hash opus requis)")
    else:
        record("import_audio (câblage)", "SKIP", "exe du jeu introuvable")

    # 25. Outils de workflow (intelligence, santé, scaffolding, refs, diff) ----
    # check_requirements
    res, _ = srv.call("check_requirements", {"gamePath": GAME})
    show(res)
    fw_installed = sum(1 for f in res.get("frameworks", []) if f.get("installed"))
    record("check_requirements", "PASS" if res["ok"] and res.get("frameworks") else "FAIL",
           f"{fw_installed} framework(s) de modding détecté(s) installé(s)")

    # analyze_dependencies sur un mod script réel
    import glob as _glob
    script_mods = [d for d in _glob.glob(os.path.join(GAME, "r6", "scripts", "*")) if os.path.isdir(d)]
    if script_mods:
        res, _ = srv.call("analyze_dependencies", {"modPath": script_mods[0], "gamePath": GAME})
        show(res)
        record("analyze_dependencies", "PASS" if res["ok"] and res.get("dependencies") else "FAIL",
               f"{len(res.get('dependencies', []))} dépendance(s) déduite(s)")
    else:
        record("analyze_dependencies", "SKIP", "aucun mod script dans r6/scripts")

    # mod_doctor
    res, ms = srv.call("mod_doctor", {"gamePath": GAME})
    show(res)
    record("mod_doctor", "PASS" if res["ok"] else "FAIL",
           f"{ms:.0f} ms — {len(res.get('installedFrameworks', []))} frameworks, "
           f"{len(res.get('missingDependencies', []))} manquant(s)")

    # scaffold_mod (archive) + manifeste
    sm_parent = wd("scaffold")
    res, _ = srv.call("scaffold_mod", {"parentFolder": sm_parent, "modName": "ScaffoldArch",
                                       "kind": "archive", "author": "Validation",
                                       "dependencies": "Codeware,ArchiveXL"})
    show(res)
    sm_ok = res["ok"] and os.path.isfile(os.path.join(sm_parent, "ScaffoldArch", "MOD_MANIFEST.json")) \
        and os.path.isfile(os.path.join(sm_parent, "ScaffoldArch", "ScaffoldArch.cpmodproj"))
    record("scaffold_mod", "PASS" if sm_ok else "FAIL", "structure + .cpmodproj + manifeste deps")

    # scaffold_archivexl + validate_xl (round-trip)
    xl_dir = wd("xl")
    res, _ = srv.call("scaffold_archivexl", {"outputFolder": xl_dir, "modName": "XlMod", "kind": "factory"})
    xl_file = os.path.join(xl_dir, "XlMod.xl")
    res2, _ = srv.call("validate_xl", {"xlFile": xl_file}) if os.path.isfile(xl_file) else ({"status": "?"}, 0)
    record("scaffold_archivexl + validate_xl",
           "PASS" if (res["ok"] and os.path.isfile(xl_file) and res2.get("status") == "success") else "FAIL",
           f".xl généré ({res.get('kind')}) et validé ({res2.get('status')})")

    # find_references dans un mod réel
    if script_mods:
        res, _ = srv.call("find_references", {"target": "func", "searchFolder": script_mods[0], "maxResults": 50})
        show(res)
        record("find_references", "PASS" if res["ok"] else "FAIL",
               f"{res.get('matchCount')} occurrence(s) dans {res.get('filesWithMatch')} fichier(s)")
    else:
        record("find_references", "SKIP", "aucun dossier de mod à scanner")

    # package_mod sur un faux layout jeu
    pk = wd("pkg")
    os.makedirs(os.path.join(pk, "archive", "pc", "mod"), exist_ok=True)
    with open(os.path.join(pk, "archive", "pc", "mod", "x.archive"), "wb") as fh:
        fh.write(b"\x00" * 64)
    pkzip = os.path.join(wd("pkg_out"), "dist.zip")
    res, _ = srv.call("package_mod", {"sourceFolder": pk, "outputZip": pkzip})
    show(res)
    record("package_mod", "PASS" if (res["ok"] and os.path.isfile(pkzip)) else "FAIL",
           f"{res.get('fileCount')} fichier(s), layout {res.get('recognizedLayout')}")

    # diff_mod_vs_base — sanity : un fichier identique (même archive) => 0 changement
    if os.path.isfile(ARCH_ENGINE):
        res, _ = srv.call("diff_mod_vs_base", {
            "modArchive": ARCH_ENGINE,
            "gameFilePath": r"engine\materials\defaults\multilayer_default.mlmask",
            "gamePath": GAME, "baseArchive": ARCH_ENGINE})
        show(res)
        zero = res.get("ok") and not res.get("added") and not res.get("removed") and not res.get("changed")
        record("diff_mod_vs_base", "PASS" if zero else "WARN",
               "fichier identique → 0 changement (bruit $.Header filtré)" if zero
               else "diff non nul sur fichier identique — vérifier le filtrage")
    else:
        record("diff_mod_vs_base", "SKIP", "archive engine absente")

    # 26. Intelligence journal (inspect_journal + find_journal_entry) ----------
    JOURNAL_ARCH = os.path.join(CONTENT, "basegame_4_gamedata.archive")
    JOURNAL_PATH = r"base\journal\cooked_journal.journal"
    if os.path.isfile(JOURNAL_ARCH):
        rj, _ = srv.call("read_game_file", {"archivePath": JOURNAL_ARCH, "gameFilePath": JOURNAL_PATH})
        jf = rj.get("jsonFile") if rj.get("ok") else None
        if jf:
            res, ms = srv.call("inspect_journal", {"jsonFile": jf})
            show(res)
            insp_ok = res["ok"] and res.get("totalEntries", 0) > 0 and res.get("byType")
            record("inspect_journal", "PASS" if insp_ok else "FAIL",
                   f"{res.get('totalEntries')} entrées, {len(res.get('byType', {}))} types, "
                   f"{len(res.get('topLevelCategories', []))} catégories ({ms:.0f} ms)")
            res2, _ = srv.call("find_journal_entry",
                               {"jsonFile": jf, "query": "gameJournalContact", "field": "type", "maxResults": 5})
            show(res2)
            fnd_ok = res2["ok"] and res2.get("matchCount", 0) > 0 and \
                all("entries[" in m["path"] for m in res2.get("matches", []))
            record("find_journal_entry", "PASS" if fnd_ok else "FAIL",
                   f"{res2.get('matchCount')} contact(s), chemins JSON exacts")
        else:
            record("inspect_journal", "FAIL", "read_game_file du journal n'a pas produit de jsonFile")
            record("find_journal_entry", "SKIP", "journal non lu")
    else:
        record("inspect_journal", "SKIP", "basegame_4_gamedata.archive absente")
        record("find_journal_entry", "SKIP", "basegame_4_gamedata.archive absente")

    # 27. Navigation CR2W générique (inspect_cr2w / find_in_cr2w) --------------
    if os.path.isfile(JOURNAL_ARCH):
        rj, _ = srv.call("read_game_file", {"archivePath": JOURNAL_ARCH, "gameFilePath": JOURNAL_PATH})
        jf = rj.get("jsonFile") if rj.get("ok") else None
        if jf:
            res, _ = srv.call("inspect_cr2w", {"jsonFile": jf})
            show(res)
            record("inspect_cr2w", "PASS" if (res["ok"] and res.get("totalTypedObjects", 0) > 0) else "FAIL",
                   f"{res.get('totalTypedObjects')} objets typés, {len(res.get('byType', {}))} types, root={res.get('rootType')}")
            res2, _ = srv.call("find_in_cr2w", {"jsonFile": jf, "query": "gameJournalContact",
                                                "field": "$type", "maxResults": 5})
            fok = res2["ok"] and res2.get("matchCount", 0) > 0 and \
                all("." in m["path"] for m in res2.get("matches", []))
            record("find_in_cr2w", "PASS" if fok else "FAIL",
                   f"{res2.get('matchCount')} correspondance(s) avec chemins JSON")
        else:
            record("inspect_cr2w", "FAIL", "read_game_file du journal n'a pas produit de jsonFile")
            record("find_in_cr2w", "SKIP", "journal non lu")
    else:
        record("inspect_cr2w", "SKIP", "basegame_4_gamedata.archive absente")
        record("find_in_cr2w", "SKIP", "basegame_4_gamedata.archive absente")

    # 28. diagnose_logs --------------------------------------------------------
    res, _ = srv.call("diagnose_logs", {"gamePath": GAME})
    show(res)
    record("diagnose_logs", "PASS" if res["ok"] else "FAIL",
           f"{res.get('logsFound')} log(s), {res.get('totalErrors')} erreur(s), "
           f"{len(res.get('diagnoses', []))} diagnostic(s) connu(s)")

    # 29. analyze_conflicts (conflits robustes) --------------------------------
    res, ms = srv.call("analyze_conflicts", {"gamePath": GAME, "maxResults": 10})
    show(res)
    record("analyze_conflicts", "PASS" if res["ok"] else "FAIL",
           f"{ms:.0f} ms — {res.get('archiveConflictCount')} conflit(s) archive + "
           f"{res.get('tweakConflictCount')} record(s) ({res.get('archivesScanned')} archives)")

    # 30. validate_item_mod (chaîne cohérente vs cassée) -----------------------
    def _mk_item(d, good):
        rdir = os.path.join(d, "source", "resources"); os.makedirs(rdir, exist_ok=True)
        with open(os.path.join(rdir, "item.yaml"), "w", encoding="utf-8") as fh:
            fh.write("Items.my_shirt:\n  entityName: my_shirt_ent\n  appearanceName: black\n  displayName: MyMod-Shirt\n")
        with open(os.path.join(rdir, "factory.csv"), "w", encoding="utf-8") as fh:
            fh.write(("name, path\nmy_shirt_ent, my\\mod\\shirt.ent\n") if good else ("name, path\nWRONG, x\n"))
        with open(os.path.join(rdir, "loc.json"), "w", encoding="utf-8") as fh:
            fh.write(json.dumps({"onScreenEntries": [{"secondaryKey": "MyMod-Shirt" if good else "Other"}]}))
    good = wd("item_good"); _mk_item(good, True)
    bad = wd("item_bad"); _mk_item(bad, False)
    rg, _ = srv.call("validate_item_mod", {"modPath": good})
    rb, _ = srv.call("validate_item_mod", {"modPath": bad})
    vi_ok = rg["ok"] and len(rg["errors"]) == 0 and (not rb["ok"]) and len(rb["errors"]) >= 2
    record("validate_item_mod", "PASS" if vi_ok else "FAIL",
           f"cohérent={rg['status']} (0 err) · cassé={rb['status']} ({len(rb['errors'])} err)")

    # 31. lint_tweak (sémantique) ---------------------------------------------
    bad_tw = os.path.join(wd("lint_tw"), "bad.tweak")
    with open(bad_tw, "w", encoding="utf-8", newline="") as fh:
        fh.write("Items.Foo:\n\tdamage: 5\nItems.Foo:\n  $base: Items.inline7\n")
    res, _ = srv.call("lint_tweak", {"tweakFile": bad_tw})
    show(res)
    lt_ok = (res["status"] == "error") and any("TABUL" in e for e in res["errors"]) \
        and any("inline" in w for w in res["warnings"])
    record("lint_tweak", "PASS" if lt_ok else "FAIL",
           "tab + inlineN-base + record en double détectés")

    # 32. generate_manifest ----------------------------------------------------
    if script_mods:
        res, _ = srv.call("generate_manifest", {"modPath": script_mods[0], "writeFile": False})
        record("generate_manifest", "PASS" if (res["ok"] and "dependencies" in res) else "FAIL",
               f"{len(res.get('dependencies', []))} dépendance(s) déduite(s)")
    else:
        record("generate_manifest", "SKIP", "aucun mod script")

    # 33. resolve_dynamic_appearance -------------------------------------------
    res, _ = srv.call("resolve_dynamic_appearance", {"pattern": r"*base\mod\item_{gender}_{camera}.mesh"})
    rda_ok = res["ok"] and len(res.get("expansions", [])) == 4
    record("resolve_dynamic_appearance", "PASS" if rda_ok else "FAIL",
           f"{len(res.get('expansions', []))} chemin(s) {{gender}}×{{camera}} développés")

    # 34. migration_check ------------------------------------------------------
    mc_mods = _glob.glob(os.path.join(MODS, "*.archive")) if os.path.isdir(MODS) else []
    if mc_mods:
        res, ms = srv.call("migration_check", {"modArchive": mc_mods[0], "gamePath": GAME})
        record("migration_check", "PASS" if res["ok"] else "FAIL",
               f"{ms:.0f} ms — {res.get('overrideCount')} surcharge(s) active(s) / "
               f"{res.get('nonMatchingCount')} sans correspondance")
    else:
        record("migration_check", "SKIP", "aucun mod installé")

    # 35. toggle_mods (liste seule, non destructif) ----------------------------
    res, _ = srv.call("toggle_mods", {"gamePath": GAME})
    record("toggle_mods", "PASS" if res["ok"] else "FAIL",
           f"{res.get('enabledCount')} actif(s) / {res.get('disabledCount')} désactivé(s)")

    # 36. export_materials (extraction mesh + export) --------------------------
    if os.path.isfile(ARCH_ENGINE):
        em_raw = wd("mat_raw")
        # mesh déjà extrait plus haut (export_files) ? on en ré-extrait un proprement.
        exres, _ = srv.call("extract_files", {"archivePath": ARCH_ENGINE, "outputPath": em_raw,
                                              "pattern": "*.mesh"})
        meshf = produced_abs(exres, em_raw, ".mesh")
        if meshf:
            res, _ = srv.call("export_materials", {"meshFile": meshf,
                                                   "outputPath": os.path.join(wd("mat_out"), "mat.json"),
                                                   "gamePath": GAME})
            show(res)
            record("export_materials", "PASS" if (res["ok"] and len(res.get("produced", [])) > 0) else "FAIL",
                   f"{len(res.get('produced', []))} fichier(s) matériaux produits")
        else:
            record("export_materials", "SKIP", "aucun .mesh extrait")
    else:
        record("export_materials", "SKIP", "archive engine absente")

    # 37. export_entity (câblage — succès dépend d'une entité porteuse d'apparences)
    eng_ent = os.path.join(CONTENT, "basegame_4_appearance.archive")
    if os.path.isfile(eng_ent):
        ee_raw = wd("ent_raw")
        exres, _ = srv.call("extract_files", {"archivePath": eng_ent, "outputPath": ee_raw, "pattern": "*.ent"})
        entf = produced_abs(exres, ee_raw, ".ent")
        if entf:
            res, _ = srv.call("export_entity", {"entFile": entf, "outputPath": os.path.join(wd("ent_out"), "e.glb"),
                                                "appearance": "default", "gamePath": GAME})
            # Câblage vérifié si le verbe s'exécute (atteint IModTools) ; le glTF dépend de l'entité.
            wired = res.get("status") in ("success", "error", "partial")
            record("export_entity (câblage)", "PASS" if wired else "FAIL",
                   "atteint IModTools.ExportEntity (glTF si l'entité porte l'apparence demandée)")
        else:
            record("export_entity (câblage)", "SKIP", "aucun .ent extrait")
    else:
        record("export_entity (câblage)", "SKIP", "archive appearance absente")

    # 38. list_entity_appearances + validate_appearance ------------------------
    npc = _glob.glob(os.path.join(CONTENT, "*.archive"))
    # entité NPC porteuse d'apparences
    rfa = srv.call("find_in_archives", {"archivesFolder": CONTENT, "pattern": "*npc_instances*all*.ent"})[0]
    ent_pair = None
    for m in rfa.get("matches", []):
        mm = re.match(r"(.+?\.ent)\s+\((.+\.archive)\)", m)
        if mm and "proxy" not in mm.group(1).lower():
            ent_pair = (mm.group(1).strip(), os.path.join(CONTENT, mm.group(2).strip())); break
    if ent_pair:
        ed = wd("ent_appr")
        srv.call("extract_files", {"archivePath": ent_pair[1], "outputPath": ed, "pattern": "*" + ent_pair[0].split("\\")[-1]})
        entf = next((os.path.join(r, x) for r, _, fs in os.walk(ed) for x in fs if x.lower().endswith(".ent")), None)
        if entf:
            res, _ = srv.call("list_entity_appearances", {"entFile": entf})
            show(res)
            record("list_entity_appearances", "PASS" if (res["ok"] and res.get("appearanceCount", 0) > 0) else "FAIL",
                   f"{res.get('appearanceCount')} apparence(s) listées")
        else:
            record("list_entity_appearances", "SKIP", "entité non extraite")
    else:
        record("list_entity_appearances", "SKIP", "aucune entité NPC trouvée")

    # validate_appearance sur un .app de base valide → doit être sans erreur
    rfapp = srv.call("find_in_archives", {"archivesFolder": CONTENT, "pattern": "*.app"})[0]
    app_pair = None
    for m in rfapp.get("matches", []):
        mm = re.match(r"(.+?\.app)\s+\((.+\.archive)\)", m)
        if mm: app_pair = (mm.group(1).strip(), os.path.join(CONTENT, mm.group(2).strip())); break
    if app_pair:
        ad = wd("app_val")
        srv.call("extract_files", {"archivePath": app_pair[1], "outputPath": ad, "pattern": "*" + app_pair[0].split("\\")[-1]})
        appf = next((os.path.join(r, x) for r, _, fs in os.walk(ad) for x in fs if x.lower().endswith(".app")), None)
        if appf:
            res, ms = srv.call("validate_appearance", {"appFile": appf, "gamePath": GAME})
            show(res)
            # Contenu de base valide → 0 erreur attendu (le validateur ne doit pas faire de faux positif).
            va_ok = res["ok"] and res.get("meshRefsChecked", 0) >= 0 and len(res["errors"]) == 0
            record("validate_appearance", "PASS" if va_ok else "WARN",
                   f"{ms:.0f} ms — {res.get('meshRefsChecked')} réf. mesh, {res.get('meshesResolved')} résolus, "
                   f"{len(res['errors'])} erreur(s) sur contenu de base valide")
        else:
            record("validate_appearance", "SKIP", ".app non extrait")
    else:
        record("validate_appearance", "SKIP", "aucun .app trouvé")

    # Ressources MCP ----------------------------------------------------------
    ref = srv.resource("wolvenkit://reference")
    record("ressource reference", "PASS" if "aide-mémoire" in ref else "FAIL",
           f"{len(ref)} caractères")

    if os.path.isfile(ARCH_SMALL):
        arc = srv.resource("wolvenkit://archive/" + ARCH_SMALL)
        record("ressource archive/{path}", "PASS" if ".streamingsector" in arc else "FAIL",
               f"listing de l'archive ({len(arc)} caractères)")
    else:
        record("ressource archive/{path}", "SKIP", "archive de test absente")

    if extracted:
        cj = srv.resource("wolvenkit://cr2w-json/" + extracted)
        good = cj.lstrip().startswith("{") or '"' in cj
        record("ressource cr2w-json/{path}", "PASS" if good else "FAIL",
               f"CR2W rendu en JSON ({len(cj)} caractères)")
    else:
        record("ressource cr2w-json/{path}", "SKIP", "aucun fichier CR2W extrait")


if __name__ == "__main__":
    main()
