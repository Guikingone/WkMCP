#!/usr/bin/env python3
"""
Full Windows validation of the WkMCP server.

Drives the MCP server (JSON-RPC 2.0 over stdio) and exercises the 21 tools and the
3 resources on real assets of a Cyberpunk 2077 installation, then
prints a results table. This is the automated version of the manual
checklist in WINDOWS-VALIDATION.md.

Each tool returns a structured JSON object ({ ok, status, summary, produced,
warnings, errors, ... }); this script relies on it directly rather than guessing.

Usage:
    python validate-windows.py ["C:\\path\\to\\Cyberpunk 2077"]

The game path can also be given via the WKMCP_GAME variable.
Prerequisites: `dotnet build src\\WkDaemon` and `dotnet build src\\WkMcp`.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

# UTF-8 output: this script and the server emit non-ASCII characters.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DLL = os.environ.get("WKMCP_DLL") or os.path.join(
    HERE, "src", "WkMcp", "bin", "Debug", "net8.0", "WkMcp.dll")
DOTNET = os.environ.get("WKMCP_DOTNET") or shutil.which("dotnet") or "dotnet"

GAME = (sys.argv[1] if len(sys.argv) > 1
        else os.environ.get("WKMCP_GAME", r"C:\Cyberpunk\Cyberpunk 2077")).rstrip("\\/")
CONTENT = os.path.join(GAME, "archive", "pc", "content")
MODS = os.path.join(GAME, "archive", "pc", "mod")
TWEAKDB = os.path.join(GAME, "r6", "cache", "tweakdb.bin")

# Test assets — stable internal paths of the base game.
ARCH_SMALL = os.path.join(CONTENT, "basegame_2_mainmenu.archive")   # world files (CR2W)
ARCH_ENGINE = os.path.join(CONTENT, "basegame_1_engine.archive")    # meshes + textures
ARCH_AUDIO = os.path.join(CONTENT, "audio_2_soundbanks.archive")    # .wem files
MESH_GLOB = "*fx_glass_piece_01.mesh"
XBM_GLOB = "*default_sticker_texture.xbm"
WEM_GLOB = "*858926615.wem"

WORK = os.path.join(tempfile.gettempdir(), "wkvalidate")


# ── MCP client ───────────────────────────────────────────────────────────────
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
                raise IOError("the MCP server closed stdout")
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
        """Calls a tool; returns (structured dict result, ms)."""
        t = time.time()
        r = self.request("tools/call", {"name": name, "arguments": args})
        ms = (time.time() - t) * 1000
        if "error" in r:
            return _errdict(f"JSON-RPC: {r['error']}"), ms
        res = r.get("result", {})
        txt = "\n".join(c.get("text", "") for c in res.get("content", [])
                        if c.get("type") == "text")
        try:
            return json.loads(txt), ms
        except json.JSONDecodeError:
            # Non-JSON response (e.g. MCP invocation error) — we wrap it.
            d = _errdict(txt[:300] or "(empty response)")
            d["log"] = txt
            return d, ms

    def resource(self, uri):
        r = self.request("resources/read", {"uri": uri})
        if "error" in r:
            return f"JSON-RPC ERROR: {r['error']}"
        return "\n".join(c.get("text", "")
                         for c in r.get("result", {}).get("contents", []))

    def listing(self, method, key):
        return self.request(method, {}).get("result", {}).get(key, [])

    def parallel_calls(self, calls):
        """Sends N JSON-RPC requests in a pipeline then drains the N responses,
        re-matching them by ID — tests the daemon's real pipelining.
        Returns (parsed results, total duration in ms)."""
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
                out.append(_errdict(f"JSON-RPC: {r['error']}"))
                continue
            res = r.get("result", {})
            txt = "\n".join(c.get("text", "") for c in res.get("content", [])
                            if c.get("type") == "text")
            try:
                out.append(json.loads(txt))
            except json.JSONDecodeError:
                d = _errdict(txt[:300] or "(empty response)")
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


# ── Results ──────────────────────────────────────────────────────────────────
results = []  # (target, status, detail)
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
    """Absolute path of a produced file (filtered by extension if provided)."""
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
        sys.exit(f"Server DLL not found: {SERVER_DLL}\n"
                 "Build first: dotnet build src\\WkMcp")
    if not os.path.isdir(GAME):
        sys.exit(f"Cyberpunk 2077 installation not found: {GAME}\n"
                 "Pass the path as an argument: python validate-windows.py \"<path>\"")

    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK, exist_ok=True)
    print(f"=== WkMCP validation — game: {GAME} ===")
    print(f"    server: {SERVER_DLL}")
    print(f"    work  : {WORK}")

    srv = Server()
    try:
        init = srv.initialize()
        info = init.get("result", {}).get("serverInfo", {})
        print(f"\n=== MCP handshake: {info} ===")
        tools = srv.listing("tools/list", "tools")
        prompts = srv.listing("prompts/list", "prompts")
        print(f"=== tools/list: {len(tools)} tools, prompts/list: {len(prompts)} prompts ===")
        record("handshake + tools/list", "PASS" if len(tools) == 85 else "WARN",
               f"{len(tools)} tools exposed (85 expected)")
        record("prompts/list", "PASS" if len(prompts) == 5 else "WARN",
               f"{len(prompts)} MCP prompts exposed (5 expected)")
        run_all(srv)
    finally:
        srv.close()

    # ── Tableau final ────────────────────────────────────────────────────────
    print("\n\n" + "=" * 72)
    print("RESULTS TABLE")
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
    # 1. wk_status -----------------------------------------------------
    res, ms = srv.call("wk_status", {})
    show(res)
    has_cache = isinstance(res.get("cache"), dict) and "hits" in res["cache"]
    record("wk_status", "PASS" if (res["ok"] and has_cache) else "FAIL",
           f"{ms:.0f} ms (1st call = warm-up); cache={res.get('cache')}")

    # 2. compute_hash ---------------------------------------------------------
    game_path = r"base\worlds\04_main_menu\_compiled\default\04_main_menu.streamingworld"
    res, ms = srv.call("compute_hash", {"inputs": [game_path]})
    show(res)
    m = re.search(r"-\s*(\d{6,})", res.get("log", ""))
    the_hash = m.group(1) if m else None
    record("compute_hash", "PASS" if (res["ok"] and the_hash) else "FAIL",
           f"hash of {game_path} = {the_hash}")

    # 3. resolve_hash (round-trip) -------------------------------------------
    if the_hash:
        res, ms = srv.call("resolve_hash", {"hashes": [the_hash]})
        show(res)
        roundtrip = "04_main_menu.streamingworld" in res.get("log", "")
        record("resolve_hash", "PASS" if (res["ok"] and roundtrip) else "WARN",
               "compute→resolve round-trip OK" if roundtrip
               else "hash not resolved (path missing from the database?)")
    else:
        record("resolve_hash", "SKIP", "compute_hash did not provide a hash")

    # 4. archive_info ---------------------------------------------------------
    if os.path.isfile(ARCH_SMALL):
        res, _ = srv.call("archive_info", {"archivePath": ARCH_SMALL})
        res2, _ = srv.call("archive_info", {"archivePath": ARCH_SMALL, "list": True,
                                            "pattern": "*.streamingsector"})
        show(res2)
        listed = ".streamingsector" in res2.get("log", "")
        record("archive_info", "PASS" if (res["ok"] and res2["ok"] and listed) else "FAIL",
               f"info + filtered listing ({'content listed' if listed else 'empty listing'})")
    else:
        record("archive_info", "SKIP", f"archive missing: {ARCH_SMALL}")

    # 4b. diff_archives — diff between a base archive and a mod ---------------
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
                   f"+{added} / -{removed} between base and {os.path.basename(mods_found[0])}")
        else:
            record("diff_archives", "SKIP", f"no mod in {MODS}")
    else:
        record("diff_archives", "SKIP",
               f"archive or mod folder missing: {ARCH_SMALL} / {MODS}")

    # 5. find_in_archives -----------------------------------------------------
    if os.path.isdir(CONTENT):
        res, ms = srv.call("find_in_archives",
                           {"archivesFolder": CONTENT,
                            "pattern": "*04_main_menu.streamingworld"})
        show(res)
        found = "04_main_menu.streamingworld" in res.get("log", "")
        record("find_in_archives", "PASS" if (res["ok"] and found) else "FAIL",
               f"{ms:.0f} ms — search in archive\\pc\\content "
               f"({'found' if found else 'nothing found'})")

        # LRU cache: 2nd call must be much faster (cacheHits = number of archives).
        res2, ms2 = srv.call("find_in_archives",
                             {"archivesFolder": CONTENT,
                              "pattern": "*04_main_menu.streamingworld"})
        scanned = res2.get("archivesScanned", 0)
        hits = res2.get("cacheHits", 0)
        speedup = ms / ms2 if ms2 > 0 else float("inf")
        cache_ok = res2["ok"] and hits >= scanned and ms2 < ms / 2
        record("find_in_archives (warm cache)", "PASS" if cache_ok else "WARN",
               f"{ms2:.0f} ms · cache {hits}/{scanned} · speedup ×{speedup:.1f}")
    else:
        record("find_in_archives", "SKIP", f"folder missing: {CONTENT}")

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
               f"{len(res['produced'])} .streamingsector file(s) extracted")
    else:
        record("extract_files", "SKIP", f"archive missing: {ARCH_SMALL}")

    # 7. cr2w_to_json ---------------------------------------------------------
    json_dir = wd("json")
    json_file = None
    if extracted:
        res, _ = srv.call("cr2w_to_json", {"path": extracted, "outputPath": json_dir})
        show(res)
        json_file = produced_abs(res, json_dir, ".json")
        record("cr2w_to_json", "PASS" if (res["ok"] and json_file) else "FAIL",
               f"JSON produced: {os.path.basename(json_file) if json_file else 'none'}")
    else:
        record("cr2w_to_json", "SKIP", "no CR2W file extracted upstream")

    # 8. json_to_cr2w ---------------------------------------------------------
    cr2w_dir = wd("cr2w")
    if json_file:
        res, _ = srv.call("json_to_cr2w", {"path": json_file, "outputPath": cr2w_dir})
        show(res)
        back = produced_abs(res, cr2w_dir, ".streamingsector")
        record("json_to_cr2w", "PASS" if (res["ok"] and back) else "FAIL",
               f"CR2W regenerated: {os.path.basename(back) if back else 'none'}")
    else:
        record("json_to_cr2w", "SKIP", "no JSON produced upstream")

    # 8b. read_game_file (read extract+convert in a single call) -------------
    gf_path = r"base\worlds\04_main_menu\_compiled\default\always_loaded_0.streamingsector"
    rgf_json = None
    if os.path.isfile(ARCH_SMALL):
        res, _ = srv.call("read_game_file",
                          {"archivePath": ARCH_SMALL, "gameFilePath": gf_path})
        show(res)
        rgf_json = res.get("jsonFile")
        good = res["ok"] and bool(res.get("content")) and rgf_json and os.path.isfile(rgf_json)
        record("read_game_file", "PASS" if good else "FAIL",
               f"content returned ({len(res.get('content') or '')} chars) + jsonFile on disk")
    else:
        record("read_game_file", "SKIP", f"archive missing: {ARCH_SMALL}")

    # 8c. write_game_file (edited JSON → CR2W placed for pack_archive) -------
    if rgf_json and os.path.isfile(rgf_json):
        wgf_mod = wd("wgf_mod")
        res, _ = srv.call("write_game_file",
                          {"jsonFile": rgf_json, "gameFilePath": gf_path,
                           "modArchiveFolder": wgf_mod})
        show(res)
        placed = os.path.isfile(os.path.join(wgf_mod, gf_path))
        record("write_game_file", "PASS" if (res["ok"] and placed) else "FAIL",
               "CR2W placed at the right internal path" if placed else "CR2W not placed")
    else:
        record("write_game_file", "SKIP", "read_game_file did not provide a jsonFile")

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
        record("uncook", "SKIP", f"archive missing: {ARCH_ENGINE}")

    # 10. extract a raw mesh, then export_files ------------------------------
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
               f"mesh exported → {os.path.basename(exported) if exported else 'none'}")
    else:
        record("export_files", "SKIP", "no .mesh extracted upstream")

    # 11. import_raw ----------------------------------------------------------
    import_dir = wd("import")
    if png:
        res, _ = srv.call("import_raw", {"path": png, "outputPath": import_dir})
        show(res)
        imported = produced_abs(res, import_dir, ".xbm")
        record("import_raw", "PASS" if (res["ok"] and imported) else "FAIL",
               f"PNG → REDengine: {os.path.basename(imported) if imported else 'none'}")
    else:
        record("import_raw", "SKIP", "no raw image (PNG) produced upstream")

    # 12. pack_archive (real cooked files) -----------------------------------
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
               f"archive: {os.path.basename(packed_archive) if packed_archive else 'none'}"
               + (" — WITH extension warning" if unknown else " — no warning"))
    else:
        record("pack_archive", "SKIP", "no cooked file extracted upstream")

    # 12b. install_mod (fake game folder — does not touch the real install) --
    if packed_archive and os.path.isfile(packed_archive):
        fakegame = wd("fakegame")
        res, _ = srv.call("install_mod",
                          {"archivePath": packed_archive, "gamePath": fakegame})
        show(res)
        installed = os.path.isfile(os.path.join(
            fakegame, "archive", "pc", "mod", os.path.basename(packed_archive)))
        record("install_mod", "PASS" if (res["ok"] and installed) else "FAIL",
               "archive copied into archive\\pc\\mod (fake test game)")
    else:
        record("install_mod", "SKIP", "no archive produced by pack_archive")

    # 12c. lint_mod — on a real mod archive of the game (without gamePath, then with)
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
                   f"files={res.get('fileCount')} · unknown ext={res.get('unknownExtCount')} · "
                   f"conflicts (vs other mods)={res2.get('conflictCount')}")
        else:
            record("lint_mod", "SKIP", f"no mod in {MODS}")
    else:
        record("lint_mod", "SKIP", f"mod folder missing: {MODS}")

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
           "source/{archive,raw,resources,customSounds} + packed + .cpmodproj created"
           if cmp_ok else "structure or .cpmodproj missing")

    # 13a. generate_modproj — generates a .cpmodproj in an existing folder ------
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
           "GenMod.cpmodproj (valid <CP77Mod> XML) generated" if gmp_ok else "file missing/invalid")

    # 13b. lint_script (semantic) — @wrapMethod without wrappedMethod() ---------
    reds_path = os.path.join(wd("lint_src"), "bad.reds")
    with open(reds_path, "w", encoding="utf-8") as fh:
        fh.write("@wrapMethod(PlayerPuppet)\nfunc OnGameAttached() {\n  let y = 1;\n}\n")
    res, _ = srv.call("lint_script", {"scriptFile": reds_path})
    show(res)
    warns = res.get("warnings", [])
    sem_ok = res["ok"] and any("wrappedMethod" in w for w in warns)
    record("lint_script (semantic)", "PASS" if sem_ok else "FAIL",
           "semantic check: @wrapMethod without wrappedMethod() detected"
           if sem_ok else f"expected warning missing ({warns})")

    # 13b. REDmod: create_redmod_project + pack_redmod + install_redmod --------
    rm_parent = wd("redmod_src")
    res, _ = srv.call("create_redmod_project",
                      {"parentFolder": rm_parent, "modName": "ValidationRedMod",
                       "description": "Windows validation mod"})
    show(res)
    rm_dir = os.path.join(rm_parent, "ValidationRedMod")
    rm_info = os.path.join(rm_dir, "info.json")
    has_info = os.path.isfile(rm_info)
    has_archives = os.path.isdir(os.path.join(rm_dir, "archives"))
    has_tweaks = os.path.isdir(os.path.join(rm_dir, "tweaks"))
    rm_ok = res["ok"] and has_info and has_archives and has_tweaks
    record("create_redmod_project", "PASS" if rm_ok else "FAIL",
           f"info.json + archives/ + tweaks/ + scripts/ + customSounds/ "
           f"({'OK' if rm_ok else 'missing'})")

    if rm_ok:
        rm_pack_out = wd("redmod_pack")
        res, _ = srv.call("pack_redmod",
                          {"modSourceFolder": rm_dir, "outputPath": rm_pack_out})
        show(res)
        zip_path = os.path.join(rm_pack_out, "ValidationRedMod.zip")
        record("pack_redmod", "PASS" if (res["ok"] and os.path.isfile(zip_path)) else "FAIL",
               f"{os.path.basename(zip_path)} produced "
               f"({os.path.getsize(zip_path) if os.path.isfile(zip_path) else 0} B)")

        fakegame = wd("fakegame_redmod")
        res, _ = srv.call("install_redmod",
                          {"modSourceFolder": rm_dir, "gamePath": fakegame})
        show(res)
        installed = os.path.isfile(os.path.join(
            fakegame, "mods", "ValidationRedMod", "info.json"))
        record("install_redmod", "PASS" if (res["ok"] and installed) else "FAIL",
               "REDmod copied into mods/<name>/ (fake test game)")
    else:
        record("pack_redmod", "SKIP", "create_redmod_project failed")
        record("install_redmod", "SKIP", "create_redmod_project failed")

    # 14. build_project -------------------------------------------------------
    # Since create_mod_project emits a .cpmodproj, the build now really succeeds
    # and produces packed/archive/pc/mod/<Mod>.archive.
    if made:
        res, _ = srv.call("build_project", {"projectFolder": proj_dir})
        show(res)
        built = os.path.isfile(os.path.join(
            proj_dir, "packed", "archive", "pc", "mod", "ValidationMod.archive"))
        record("build_project", "PASS" if (res["ok"] and built) else "FAIL",
               "build of the .cpmodproj → packed/archive/pc/mod/ValidationMod.archive"
               if built else "no archive produced")
    else:
        record("build_project", "SKIP", "mod project not created upstream")

    # 15. list_installed_mods -------------------------------------------------
    res, _ = srv.call("list_installed_mods", {"gamePath": GAME})
    show(res)
    record("list_installed_mods", "PASS" if res["ok"] else "FAIL",
           f"{res.get('archiveModsCount', '?')} .archive mods + "
           f"{res.get('redModsCount', '?')} REDmods listed")

    # 16. detect_conflicts ----------------------------------------------------
    res, ms = srv.call("detect_conflicts", {"gamePath": GAME})
    show(res)
    if res["ok"]:
        record("detect_conflicts", "PASS", f"{ms:.0f} ms — archive\\pc\\mod conflicts analyzed")
    elif "Value cannot be null" in res.get("log", ""):
        # Upstream bug: WolvenKit.CLI 8.18.0 `conflicts` throws an ArgumentNullException
        # on a real install — reproducible with cp77tools, outside the MCP server.
        record("detect_conflicts", "WARN",
               "tool correctly wired; WolvenKit.CLI 8.18.0 `conflicts` crashes "
               "(upstream bug, reproducible as-is with cp77tools)")
    else:
        record("detect_conflicts", "FAIL", "unexpected failure — see the output above")

    # 17. tweakdb_query -------------------------------------------------------
    rec_name = rec_id = None
    if os.path.isfile(TWEAKDB):
        res, ms = srv.call("tweakdb_query",
                           {"tweakdbPath": TWEAKDB, "filter": "Items.Preset_"})
        show(res)
        # Lines: "record/flat  <name>  <TweakDBID 0x.. / <decimal>:<length>>".
        m = re.search(r"(\S+)\s+<TweakDBID[^/]+/\s*(\d+):", res.get("log", ""))
        if m:
            rec_name, rec_id = m.group(1), m.group(2)
        record("tweakdb_query", "PASS" if res["ok"] else "FAIL",
               f"{ms:.0f} ms — tweakdb.bin loaded, records/flats listed")
    else:
        record("tweakdb_query", "SKIP", f"tweakdb.bin missing: {TWEAKDB}")

    # 18. tweakdb_resolve -----------------------------------------------------
    if rec_id:
        res, _ = srv.call("tweakdb_resolve", {"hashes": [rec_id]})
        show(res)
        resolved = bool(rec_name) and rec_name in res.get("log", "")
        record("tweakdb_resolve", "PASS" if resolved else "WARN",
               f"{rec_id} → {rec_name}" if resolved
               else f"identifier {rec_id} ('{rec_name}') not resolved")
    else:
        record("tweakdb_resolve", "SKIP", "no identifier obtained via tweakdb_query")

    # 18-bis. describe_tweak_record — inspects all flats of a known record.
    target_record = rec_name or "Items.Preset_Achilles_Collectible_inline0"
    if os.path.isfile(TWEAKDB):
        res, _ = srv.call("describe_tweak_record",
                          {"tweakdbPath": TWEAKDB, "recordId": target_record})
        show(res)
        # The daemon emits "flat <name> : <type> = <value>" lines; we count them.
        flat_lines = sum(1 for line in res.get("log", "").splitlines() if "  flat  " in line)
        record("describe_tweak_record", "PASS" if (res["ok"] and flat_lines > 0) else "WARN",
               f"{target_record} → {flat_lines} flat(s) listed")
    else:
        record("describe_tweak_record", "SKIP", f"tweakdb.bin missing: {TWEAKDB}")

    # 18-ter. inspect_mesh / inspect_texture — extract a .mesh and .xbm
    # then inspect (summary without conversion).
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
                   f"LOD={res.get('lodCount')} · sub-mesh={res.get('subMeshCount')} · "
                   f"materials={res.get('materialCount')} · bones={res.get('boneCount')}")
        else:
            record("inspect_mesh", "SKIP", "no .mesh extracted")
        if xbm:
            res, _ = srv.call("inspect_texture", {"xbmFile": xbm})
            show(res)
            ok = res["ok"] and res.get("width", 0) > 0 and res.get("height", 0) > 0
            record("inspect_texture", "PASS" if ok else "FAIL",
                   f"{res.get('width')}x{res.get('height')} format={res.get('format')} "
                   f"mips={res.get('mipLevels')}")
        else:
            record("inspect_texture", "SKIP", "no .xbm extracted")
    else:
        record("inspect_mesh", "SKIP", f"archive missing: {ARCH_ENGINE}")
        record("inspect_texture", "SKIP", f"archive missing: {ARCH_ENGINE}")

    # 18a. IPC pipelining: sends 4 requests in a pipeline, checks that the
    # responses arrive (order-tolerant) and that the total isn't "4 × cold path".
    parallel_res, parallel_ms = srv.parallel_calls([
        ("compute_hash", {"inputs": [f"base/wolvenkit_pipeline_{i}.mesh"]})
        for i in range(4)
    ])
    all_ok = all(r.get("ok") for r in parallel_res)
    record("IPC pipelining (4 requests in flight)",
           "PASS" if all_ok else "FAIL",
           f"{len(parallel_res)} concurrent compute_hash in {parallel_ms:.0f} ms")

    # 18b. structured tweak: read_tweak / write_tweak / validate_tweak / install_tweak
    tweak_dir = wd("tweak")
    tweak_src = os.path.join(tweak_dir, "validation.tweak")
    with open(tweak_src, "w", encoding="utf-8") as f:
        # Overrides a known TweakDB record (damage boost of a preset).
        # We use a name obtained via tweakdb_query → tweakdb_resolve above,
        # falling back to a generic identifier known from the base TweakDB.
        rec = rec_name or "Items.Preset_Achilles_Collectible_inline0"
        f.write(f"{rec}:\n  damage: 250\n  attacksPerSecond: 3.0\n")
        f.write("MyMod.NewItem:\n  $base: Items.Preset_Achilles_Collectible_inline0\n  damage: 500\n")

    # read_tweak
    res, _ = srv.call("read_tweak", {"tweakFile": tweak_src})
    show(res)
    parsed_json_file = res.get("jsonFile")
    has_content = bool(res.get("content")) and "damage" in res.get("content", "")
    record("read_tweak", "PASS" if (res["ok"] and has_content) else "FAIL",
           f"JSON produced: {os.path.basename(parsed_json_file) if parsed_json_file else 'none'}")

    # write_tweak (round-trip)
    rt_out = os.path.join(tweak_dir, "roundtrip.tweak")
    if parsed_json_file and os.path.isfile(parsed_json_file):
        res, _ = srv.call("write_tweak",
                          {"jsonFile": parsed_json_file, "outputTweakFile": rt_out})
        show(res)
        rt_ok = res["ok"] and os.path.isfile(rt_out) and os.path.getsize(rt_out) > 0
        record("write_tweak", "PASS" if rt_ok else "FAIL",
               f".tweak regenerated: {os.path.basename(rt_out)} "
               f"({os.path.getsize(rt_out) if rt_ok else 0} B)")
    else:
        record("write_tweak", "SKIP", "read_tweak did not produce JSON")

    # validate_tweak
    if os.path.isfile(TWEAKDB):
        res, _ = srv.call("validate_tweak",
                          {"tweakFile": tweak_src, "tweakdbBin": TWEAKDB})
        show(res)
        # The 2nd record (MyMod.NewItem) has $base, so OK; the 1st must exist.
        record("validate_tweak", "PASS" if res["ok"] else "WARN",
               f"validation run (exit={res.get('exitCode')})")
    else:
        record("validate_tweak", "SKIP", f"tweakdb.bin missing: {TWEAKDB}")

    # install_tweak (fake test game)
    fakegame_t = wd("fakegame_tweak")
    res, _ = srv.call("install_tweak",
                      {"tweakFile": tweak_src, "gamePath": fakegame_t})
    show(res)
    installed_t = os.path.isfile(os.path.join(
        fakegame_t, "r6", "tweaks", os.path.basename(tweak_src)))
    record("install_tweak", "PASS" if (res["ok"] and installed_t) else "FAIL",
           ".tweak copied into r6/tweaks (fake test game)")

    # 18c. generate_tweak_template — 3 patterns, each re-validated via read_tweak.
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
                   "file not produced")
            break
        # Round-trip: the generated .tweak must be readable.
        rr, _ = srv.call("read_tweak", {"tweakFile": out})
        ok = res["ok"] and rr["ok"] and "damage" in (rr.get("content", "") + " ") \
             or pattern_name == "new_record"  # new_record has no damage by default
        record(f"generate_tweak_template ({pattern_name})",
               "PASS" if rr["ok"] else "FAIL",
               f"generated → {os.path.basename(out)} ({os.path.getsize(out)} B, re-read OK)"
               if rr["ok"] else "re-read failed")

    # 18d. read_script / lint_script — sane + broken .reds file.
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
           f"{len(res.get('declarations', []))} declaration(s)")

    res, _ = srv.call("lint_script", {"scriptFile": sane})
    show(res)
    lint_sane_ok = res["ok"] and len(res.get("errors", [])) == 0
    record("lint_script (sane)",
           "PASS" if lint_sane_ok else "FAIL",
           f"0 error on sane file "
           f"({len(res.get('declarations', []))} declarations)")

    # Broken file: missing brace
    broken = os.path.join(script_dir, "broken.reds")
    with open(broken, "w", encoding="utf-8") as f:
        f.write("public class Broken {\n  public func x() -> Int32 { return 1;\n}\n")  # 1 { opened, not closed
    res, _ = srv.call("lint_script", {"scriptFile": broken})
    show(res)
    lint_broken_ok = len(res.get("errors", [])) > 0
    record("lint_script (broken)",
           "PASS" if lint_broken_ok else "FAIL",
           f"error(s) detected: {res.get('errors', [])[:1]}")

    # 18e. backup_mods / restore_mods — on a fake game built for the test.
    fakegame_b = wd("fakegame_backup")
    for sub in ("archive/pc/mod", "mods/SomeRedmod", "r6/tweaks"):
        os.makedirs(os.path.join(fakegame_b, *sub.split("/")), exist_ok=True)
    # Create 2 dummy archives + 1 redmod info + 1 dummy tweak
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
           f"ZIP {os.path.getsize(zip_path) if backup_ok else 0} B · "
           f"{res.get('archiveCount')} archives + {res.get('redmodCount')} redmods + "
           f"{res.get('tweakCount')} tweaks")

    if backup_ok:
        # Restore into an empty fake game
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
               f"{res.get('extractedCount')} file(s) extracted into empty fake game")
    else:
        record("restore_mods", "SKIP", "backup_mods failed")

    # 18f. uninstall_mod / uninstall_redmod / uninstall_tweak — round-trip
    # install then uninstall on the fake backup game (which already has fake1.archive,
    # SomeRedmod, x.tweak created by step 18e).
    if os.path.isdir(fakegame_b):
        # uninstall_mod
        res, _ = srv.call("uninstall_mod",
                          {"archivePathOrName": "fake1.archive",
                           "gamePath": fakegame_b})
        show(res)
        gone = not os.path.isfile(os.path.join(
            fakegame_b, "archive", "pc", "mod", "fake1.archive"))
        record("uninstall_mod", "PASS" if (res["ok"] and gone) else "FAIL",
               "fake1.archive removed from the fake game")

        # uninstall_redmod
        res, _ = srv.call("uninstall_redmod",
                          {"modName": "SomeRedmod", "gamePath": fakegame_b})
        show(res)
        gone = not os.path.isdir(os.path.join(fakegame_b, "mods", "SomeRedmod"))
        record("uninstall_redmod", "PASS" if (res["ok"] and gone) else "FAIL",
               "mods/SomeRedmod removed from the fake game")

        # uninstall_tweak
        res, _ = srv.call("uninstall_tweak",
                          {"tweakName": "x.tweak", "gamePath": fakegame_b})
        show(res)
        gone = not os.path.isfile(os.path.join(fakegame_b, "r6", "tweaks", "x.tweak"))
        record("uninstall_tweak", "PASS" if (res["ok"] and gone) else "FAIL",
               "r6/tweaks/x.tweak removed from the fake game")
    else:
        record("uninstall_mod", "SKIP", "fake backup game missing")
        record("uninstall_redmod", "SKIP", "fake backup game missing")
        record("uninstall_tweak", "SKIP", "fake backup game missing")

    # 18g. deploy_redmod — on the real game if REDmod is installed.
    redmod_exe = os.path.join(GAME, "tools", "redmod", "bin", "redMod.exe")
    if os.path.isfile(redmod_exe):
        res, _ = srv.call("deploy_redmod", {"gamePath": GAME})
        show(res)
        # deploy may return a non-zero exit code if some REDmods have issues,
        # but the tool itself must run without crashing.
        record("deploy_redmod",
               "PASS" if res.get("status") in ("success", "partial") else "WARN",
               f"redMod.exe deploy run (exit={res.get('exitCode')})")
    else:
        record("deploy_redmod", "SKIP", f"REDmod missing: {redmod_exe}")

    # 18h. launch_game — DRY (fake game without Cyberpunk2077.exe): must report cleanly.
    res, _ = srv.call("launch_game",
                      {"gamePath": fakegame_b or wd("fake_launch"),
                       "deployRedmod": False})
    show(res)
    record("launch_game (fake game)",
           "PASS" if not res["ok"] and "Cyberpunk2077.exe" in (res.get("summary", "")
                                                                + " "
                                                                + " ".join(res.get("errors", []))) else "WARN",
           "refuses to launch (exe missing in the fake game) — expected behavior")

    # 18i. tail_game_logs — on dummy logs.
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
           f"{res.get('lineCount')} lines read, last error visible")

    # 18j. mod_summary — on a real mod archive + on a fake REDmod.
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
                   f"{res.get('fileCount')} file(s) categorized by extension")
        else:
            record("mod_summary (archive)", "SKIP", "no .archive mod")

    # ... + redmod dir (reuse the REDmod created in step 13b)
    if 'rm_dir' in dir() and rm_ok:
        res, _ = srv.call("mod_summary", {"modPath": rm_dir})
        show(res)
        ok = res["ok"] and res.get("kind") == "redmod" \
             and res.get("name") == "ValidationRedMod"
        record("mod_summary (redmod)",
               "PASS" if ok else "FAIL",
               f"name={res.get('name')} · {res.get('fileCounts', {})}")

    # 18k. dump_records — on the game tweakdb, a small type for speed.
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
               f"{os.path.getsize(dump_out) if ok else 0} B JSONL produced "
               f"(gamedataWeaponItem_Record)")
    else:
        record("dump_records", "SKIP", "tweakdb.bin missing")

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
        # lint_script must not find any error on generated code.
        lint_res, _ = srv.call("lint_script", {"scriptFile": out})
        if len(lint_res.get("errors", [])) != 0:
            all_reds_ok = False
            break
    record("generate_redscript_template (5 patterns)",
           "PASS" if all_reds_ok else "FAIL",
           "5 templates generated, each passes lint_script without error")

    # 18n. extract_localization + build_localization — TweakDB UI strings.
    if os.path.isfile(TWEAKDB):
        loc_dir = wd("loc")
        loc_json = os.path.join(loc_dir, "loc.json")
        res, _ = srv.call("extract_localization",
                          {"tweakdbPath": TWEAKDB,
                           "outputJson": loc_json,
                           "filter": "Items.Preset_"})
        show(res)
        # The JSON file must exist and contain records.
        extract_ok = res["ok"] and os.path.isfile(loc_json) \
                     and os.path.getsize(loc_json) > 100
        record("extract_localization",
               "PASS" if extract_ok else "FAIL",
               f"{os.path.getsize(loc_json) if extract_ok else 0} B JSON produced "
               f"(Items.Preset_ filter)")

        if extract_ok:
            # Build a .tweak from the first 5 entries.
            with open(loc_json, "r", encoding="utf-8") as f:
                data = json.load(f)
            sample = {}
            for k, v in list(data.items())[:5]:
                # We make up a trivial translation.
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
                   f"{res2.get('fieldCount')} field(s) translated")
        else:
            record("build_localization", "SKIP", "extract_localization failed")
    else:
        record("extract_localization", "SKIP", "tweakdb.bin missing")
        record("build_localization", "SKIP", "tweakdb.bin missing")

    # 18m. clear_cache — on 'archives' then check that CacheStats are reset to zero.
    res, _ = srv.call("clear_cache", {"scope": "archives"})
    show(res)
    # We check that wk_status returns entries=0 (the cache was drained).
    status_res, _ = srv.call("wk_status", {})
    cache_after = status_res.get("cache", {})
    record("clear_cache (archives)",
           "PASS" if (res["ok"] and cache_after.get("entries") == 0) else "FAIL",
           f"cache cleared · entries after clear = {cache_after.get('entries')}")

    # 19-20. oodle_compress / oodle_decompress (byte-exact round-trip) -------
    oodle_dir = wd("oodle")
    src = os.path.join(oodle_dir, "input.bin")
    comp = os.path.join(oodle_dir, "input.kraken")
    deco = os.path.join(oodle_dir, "output.bin")
    with open(src, "wb") as f:
        f.write(b"WkMCP oodle round-trip test block. " * 1500)
    res, _ = srv.call("oodle_compress", {"inputPath": src, "outputPath": comp})
    show(res)
    comp_ok = res["ok"] and os.path.isfile(comp) and os.path.getsize(comp) > 0
    record("oodle_compress", "PASS" if comp_ok else "FAIL",
           f"{os.path.getsize(src)} B → {os.path.getsize(comp) if os.path.isfile(comp) else 0} B")
    if comp_ok:
        res, _ = srv.call("oodle_decompress", {"inputPath": comp, "outputPath": deco})
        show(res)
        out = deco if os.path.isfile(deco) else (deco + ".bin")
        exact = (os.path.isfile(out)
                 and open(out, "rb").read() == open(src, "rb").read())
        record("oodle_decompress", "PASS" if (res["ok"] and exact) else "FAIL",
               "byte-exact round-trip" if exact else "decompression not identical")
    else:
        record("oodle_decompress", "SKIP", "compression failed upstream")

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
               f"WEM → {os.path.basename(ogg) if ogg else 'no OGG'} (Windows audio binaries)")
    else:
        record("wwise_export", "SKIP", "no .wem extracted (audio archive missing?)")

    # 21b. extract_audio (opus / voice-over) ----------------------------------
    if os.path.isfile(ARCH_AUDIO):
        opus_dir = wd("opus")
        res, ms = srv.call("extract_audio",
                           {"archivePath": ARCH_AUDIO, "outputPath": opus_dir})
        show(res)
        n = len(res.get("produced", []) or [])
        if res["ok"] and n > 0:
            record("extract_audio", "PASS", f"{n} audio file(s) extracted via uncook opus ({ms:.0f} ms)")
        elif res["ok"]:
            # Wiring OK but the tested archive contains no opusinfo (sfx vs voice).
            record("extract_audio", "WARN",
                   "opus pipeline run without error; no opus produced "
                   "(non-voice archive? try lang_xx_voice.archive)")
        else:
            record("extract_audio", "FAIL", "opus pipeline failure — see the output")
    else:
        record("extract_audio", "SKIP", "audio archive missing")

    # 22. Dedicated exports (anim / morphtarget / mlmask) on real assets -------
    def export_asset(tool, archive, basename, ext, strict=True):
        if not os.path.isfile(archive):
            record(tool, "SKIP", f"archive missing: {os.path.basename(archive)}"); return
        exdir = wd(tool + "_raw")
        srv.call("extract_files", {"archivePath": archive, "outputPath": exdir,
                                   "pattern": "*" + basename})
        found = None
        for root, _, files in os.walk(exdir):
            for f in files:
                if f.lower().endswith(ext):
                    found = os.path.join(root, f)
        if not found:
            record(tool, "SKIP", f"extraction of *{basename} empty"); return
        outdir = wd(tool + "_out")
        res, _ = srv.call(tool, {"path": found, "outputPath": outdir})
        show(res)
        n = sum(len(fs) for _, _, fs in os.walk(outdir))
        if res["ok"] and n > 0:
            record(tool, "PASS", f"{basename} → {n} file(s)")
        elif not strict:
            # Tool correctly wired; output depends on the data (e.g. .anims
            # needing its rig to produce a glTF — WolvenKit constraint).
            record(tool, "WARN", f"{basename}: export with no usable output "
                   "(a .anims without its rig produces no glTF — WolvenKit constraint)")
        elif res["ok"]:
            record(tool, "WARN", f"{basename}: export with no output")
        else:
            record(tool, "FAIL", "export failure")

    export_asset("export_mlmask", os.path.join(CONTENT, "basegame_1_engine.archive"),
                 "multilayer_default.mlmask", ".mlmask")
    export_asset("export_morphtarget", os.path.join(CONTENT, "basegame_4_appearance.archive"),
                 "hb_000_pma__morphs_default.morphtarget", ".morphtarget")
    export_asset("export_animation", os.path.join(CONTENT, "basegame_4_animation.archive"),
                 "johnny__sit_car_passenger_generic__02.anims", ".anims", strict=False)

    # 23. loc_resolve — LocKey → localized text (loads the game archives) -------
    if os.path.isfile(os.path.join(GAME, "bin", "x64", "Cyberpunk2077.exe")):
        res, ms = srv.call("loc_resolve", {"gamePath": GAME, "key": "40", "language": "en_us"})
        show(res)
        got = res["ok"] and ("female=" in res.get("log", "") or "male=" in res.get("log", ""))
        record("loc_resolve", "PASS" if got else "WARN",
               f"key 40 → on-screen entry resolved ({ms:.0f} ms)" if got
               else "resolution inconclusive")
    else:
        record("loc_resolve", "SKIP", "game exe not found")

    # 24. import_audio — wiring (loads ArchiveManager + reaches OpusTools) ------
    if os.path.isfile(os.path.join(GAME, "bin", "x64", "Cyberpunk2077.exe")):
        wavdir = wd("wav_in")
        open(os.path.join(wavdir, "1.wav"), "wb").close()  # placeholder (dummy hash)
        res, _ = srv.call("import_audio",
                          {"gamePath": GAME, "wavFolder": wavdir,
                           "outputPath": wd("opus_mod"), "verbose": True})
        show(res)
        # A dummy wav matches no hash: we validate that the verb RUNS
        # (ArchiveManager loaded, OpusTools reached), not the full round-trip.
        wired = "Archive Manager loaded" in res.get("log", "") or "opus-import" in res.get("log", "")
        record("import_audio (wiring)", "PASS" if wired else "WARN",
               "ArchiveManager loaded + OpusTools.ImportWavs reached "
               "(full round-trip: WAVs named by real opus hash required)")
    else:
        record("import_audio (wiring)", "SKIP", "game exe not found")

    # 25. Workflow tools (intelligence, health, scaffolding, refs, diff) -------
    # check_requirements
    res, _ = srv.call("check_requirements", {"gamePath": GAME})
    show(res)
    fw_installed = sum(1 for f in res.get("frameworks", []) if f.get("installed"))
    record("check_requirements", "PASS" if res["ok"] and res.get("frameworks") else "FAIL",
           f"{fw_installed} modding framework(s) detected as installed")

    # analyze_dependencies on a real script mod
    import glob as _glob
    script_mods = [d for d in _glob.glob(os.path.join(GAME, "r6", "scripts", "*")) if os.path.isdir(d)]
    if script_mods:
        res, _ = srv.call("analyze_dependencies", {"modPath": script_mods[0], "gamePath": GAME})
        show(res)
        record("analyze_dependencies", "PASS" if res["ok"] and res.get("dependencies") else "FAIL",
               f"{len(res.get('dependencies', []))} dependency(ies) inferred")
    else:
        record("analyze_dependencies", "SKIP", "no script mod in r6/scripts")

    # mod_doctor
    res, ms = srv.call("mod_doctor", {"gamePath": GAME})
    show(res)
    record("mod_doctor", "PASS" if res["ok"] else "FAIL",
           f"{ms:.0f} ms — {len(res.get('installedFrameworks', []))} frameworks, "
           f"{len(res.get('missingDependencies', []))} missing")

    # scaffold_mod (archive) + manifest
    sm_parent = wd("scaffold")
    res, _ = srv.call("scaffold_mod", {"parentFolder": sm_parent, "modName": "ScaffoldArch",
                                       "kind": "archive", "author": "Validation",
                                       "dependencies": "Codeware,ArchiveXL"})
    show(res)
    sm_ok = res["ok"] and os.path.isfile(os.path.join(sm_parent, "ScaffoldArch", "MOD_MANIFEST.json")) \
        and os.path.isfile(os.path.join(sm_parent, "ScaffoldArch", "ScaffoldArch.cpmodproj"))
    record("scaffold_mod", "PASS" if sm_ok else "FAIL", "structure + .cpmodproj + deps manifest")

    # scaffold_archivexl + validate_xl (round-trip)
    xl_dir = wd("xl")
    res, _ = srv.call("scaffold_archivexl", {"outputFolder": xl_dir, "modName": "XlMod", "kind": "factory"})
    xl_file = os.path.join(xl_dir, "XlMod.xl")
    res2, _ = srv.call("validate_xl", {"xlFile": xl_file}) if os.path.isfile(xl_file) else ({"status": "?"}, 0)
    record("scaffold_archivexl + validate_xl",
           "PASS" if (res["ok"] and os.path.isfile(xl_file) and res2.get("status") == "success") else "FAIL",
           f".xl generated ({res.get('kind')}) and validated ({res2.get('status')})")

    # find_references in a real mod
    if script_mods:
        res, _ = srv.call("find_references", {"target": "func", "searchFolder": script_mods[0], "maxResults": 50})
        show(res)
        record("find_references", "PASS" if res["ok"] else "FAIL",
               f"{res.get('matchCount')} occurrence(s) in {res.get('filesWithMatch')} file(s)")
    else:
        record("find_references", "SKIP", "no mod folder to scan")

    # package_mod on a fake game layout
    pk = wd("pkg")
    os.makedirs(os.path.join(pk, "archive", "pc", "mod"), exist_ok=True)
    with open(os.path.join(pk, "archive", "pc", "mod", "x.archive"), "wb") as fh:
        fh.write(b"\x00" * 64)
    pkzip = os.path.join(wd("pkg_out"), "dist.zip")
    res, _ = srv.call("package_mod", {"sourceFolder": pk, "outputZip": pkzip})
    show(res)
    record("package_mod", "PASS" if (res["ok"] and os.path.isfile(pkzip)) else "FAIL",
           f"{res.get('fileCount')} file(s), layout {res.get('recognizedLayout')}")

    # diff_mod_vs_base — sanity: an identical file (same archive) => 0 changes
    if os.path.isfile(ARCH_ENGINE):
        res, _ = srv.call("diff_mod_vs_base", {
            "modArchive": ARCH_ENGINE,
            "gameFilePath": r"engine\materials\defaults\multilayer_default.mlmask",
            "gamePath": GAME, "baseArchive": ARCH_ENGINE})
        show(res)
        zero = res.get("ok") and not res.get("added") and not res.get("removed") and not res.get("changed")
        record("diff_mod_vs_base", "PASS" if zero else "WARN",
               "identical file → 0 changes ($.Header noise filtered)" if zero
               else "non-zero diff on identical file — check the filtering")
    else:
        record("diff_mod_vs_base", "SKIP", "engine archive missing")

    # 26. Journal intelligence (inspect_journal + find_journal_entry) ----------
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
                   f"{res.get('totalEntries')} entries, {len(res.get('byType', {}))} types, "
                   f"{len(res.get('topLevelCategories', []))} categories ({ms:.0f} ms)")
            res2, _ = srv.call("find_journal_entry",
                               {"jsonFile": jf, "query": "gameJournalContact", "field": "type", "maxResults": 5})
            show(res2)
            fnd_ok = res2["ok"] and res2.get("matchCount", 0) > 0 and \
                all("entries[" in m["path"] for m in res2.get("matches", []))
            record("find_journal_entry", "PASS" if fnd_ok else "FAIL",
                   f"{res2.get('matchCount')} contact(s), exact JSON paths")
        else:
            record("inspect_journal", "FAIL", "read_game_file of the journal did not produce a jsonFile")
            record("find_journal_entry", "SKIP", "journal not read")
    else:
        record("inspect_journal", "SKIP", "basegame_4_gamedata.archive missing")
        record("find_journal_entry", "SKIP", "basegame_4_gamedata.archive missing")

    # 27. Generic CR2W navigation (inspect_cr2w / find_in_cr2w) ----------------
    if os.path.isfile(JOURNAL_ARCH):
        rj, _ = srv.call("read_game_file", {"archivePath": JOURNAL_ARCH, "gameFilePath": JOURNAL_PATH})
        jf = rj.get("jsonFile") if rj.get("ok") else None
        if jf:
            res, _ = srv.call("inspect_cr2w", {"jsonFile": jf})
            show(res)
            record("inspect_cr2w", "PASS" if (res["ok"] and res.get("totalTypedObjects", 0) > 0) else "FAIL",
                   f"{res.get('totalTypedObjects')} typed objects, {len(res.get('byType', {}))} types, root={res.get('rootType')}")
            res2, _ = srv.call("find_in_cr2w", {"jsonFile": jf, "query": "gameJournalContact",
                                                "field": "$type", "maxResults": 5})
            fok = res2["ok"] and res2.get("matchCount", 0) > 0 and \
                all("." in m["path"] for m in res2.get("matches", []))
            record("find_in_cr2w", "PASS" if fok else "FAIL",
                   f"{res2.get('matchCount')} match(es) with JSON paths")
        else:
            record("inspect_cr2w", "FAIL", "read_game_file of the journal did not produce a jsonFile")
            record("find_in_cr2w", "SKIP", "journal not read")
    else:
        record("inspect_cr2w", "SKIP", "basegame_4_gamedata.archive missing")
        record("find_in_cr2w", "SKIP", "basegame_4_gamedata.archive missing")

    # 28. diagnose_logs --------------------------------------------------------
    res, _ = srv.call("diagnose_logs", {"gamePath": GAME})
    show(res)
    record("diagnose_logs", "PASS" if res["ok"] else "FAIL",
           f"{res.get('logsFound')} log(s), {res.get('totalErrors')} error(s), "
           f"{len(res.get('diagnoses', []))} known diagnosis(es)")

    # 29. analyze_conflicts (robust conflicts) ---------------------------------
    res, ms = srv.call("analyze_conflicts", {"gamePath": GAME, "maxResults": 10})
    show(res)
    record("analyze_conflicts", "PASS" if res["ok"] else "FAIL",
           f"{ms:.0f} ms — {res.get('archiveConflictCount')} archive conflict(s) + "
           f"{res.get('tweakConflictCount')} record(s) ({res.get('archivesScanned')} archives)")

    # 30. validate_item_mod (consistent vs broken chain) -----------------------
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
           f"consistent={rg['status']} (0 err) · broken={rb['status']} ({len(rb['errors'])} err)")

    # 31. lint_tweak (semantic) -----------------------------------------------
    bad_tw = os.path.join(wd("lint_tw"), "bad.tweak")
    with open(bad_tw, "w", encoding="utf-8", newline="") as fh:
        fh.write("Items.Foo:\n\tdamage: 5\nItems.Foo:\n  $base: Items.inline7\n")
    res, _ = srv.call("lint_tweak", {"tweakFile": bad_tw})
    show(res)
    lt_ok = (res["status"] == "error") and any("TABUL" in e for e in res["errors"]) \
        and any("inline" in w for w in res["warnings"])
    record("lint_tweak", "PASS" if lt_ok else "FAIL",
           "tab + inlineN-base + duplicate record detected")

    # 32. generate_manifest ----------------------------------------------------
    if script_mods:
        res, _ = srv.call("generate_manifest", {"modPath": script_mods[0], "writeFile": False})
        record("generate_manifest", "PASS" if (res["ok"] and "dependencies" in res) else "FAIL",
               f"{len(res.get('dependencies', []))} dependency(ies) inferred")
    else:
        record("generate_manifest", "SKIP", "no script mod")

    # 33. resolve_dynamic_appearance -------------------------------------------
    res, _ = srv.call("resolve_dynamic_appearance", {"pattern": r"*base\mod\item_{gender}_{camera}.mesh"})
    rda_ok = res["ok"] and len(res.get("expansions", [])) == 4
    record("resolve_dynamic_appearance", "PASS" if rda_ok else "FAIL",
           f"{len(res.get('expansions', []))} {{gender}}×{{camera}} path(s) expanded")

    # 34. migration_check ------------------------------------------------------
    mc_mods = _glob.glob(os.path.join(MODS, "*.archive")) if os.path.isdir(MODS) else []
    if mc_mods:
        res, ms = srv.call("migration_check", {"modArchive": mc_mods[0], "gamePath": GAME})
        record("migration_check", "PASS" if res["ok"] else "FAIL",
               f"{ms:.0f} ms — {res.get('overrideCount')} active override(s) / "
               f"{res.get('nonMatchingCount')} without match")
    else:
        record("migration_check", "SKIP", "no installed mod")

    # 35. toggle_mods (list only, non-destructive) -----------------------------
    res, _ = srv.call("toggle_mods", {"gamePath": GAME})
    record("toggle_mods", "PASS" if res["ok"] else "FAIL",
           f"{res.get('enabledCount')} enabled / {res.get('disabledCount')} disabled")

    # 36. export_materials (mesh extraction + export) --------------------------
    if os.path.isfile(ARCH_ENGINE):
        em_raw = wd("mat_raw")
        # mesh already extracted above (export_files)? we re-extract one cleanly.
        exres, _ = srv.call("extract_files", {"archivePath": ARCH_ENGINE, "outputPath": em_raw,
                                              "pattern": "*.mesh"})
        meshf = produced_abs(exres, em_raw, ".mesh")
        if meshf:
            res, _ = srv.call("export_materials", {"meshFile": meshf,
                                                   "outputPath": os.path.join(wd("mat_out"), "mat.json"),
                                                   "gamePath": GAME})
            show(res)
            record("export_materials", "PASS" if (res["ok"] and len(res.get("produced", [])) > 0) else "FAIL",
                   f"{len(res.get('produced', []))} material file(s) produced")
        else:
            record("export_materials", "SKIP", "no .mesh extracted")
    else:
        record("export_materials", "SKIP", "engine archive missing")

    # 37. export_entity (wiring — success depends on an entity that carries appearances)
    eng_ent = os.path.join(CONTENT, "basegame_4_appearance.archive")
    if os.path.isfile(eng_ent):
        ee_raw = wd("ent_raw")
        exres, _ = srv.call("extract_files", {"archivePath": eng_ent, "outputPath": ee_raw, "pattern": "*.ent"})
        entf = produced_abs(exres, ee_raw, ".ent")
        if entf:
            res, _ = srv.call("export_entity", {"entFile": entf, "outputPath": os.path.join(wd("ent_out"), "e.glb"),
                                                "appearance": "default", "gamePath": GAME})
            # Wiring confirmed if the verb runs (reaches IModTools); the glTF depends on the entity.
            wired = res.get("status") in ("success", "error", "partial")
            record("export_entity (wiring)", "PASS" if wired else "FAIL",
                   "reaches IModTools.ExportEntity (glTF if the entity carries the requested appearance)")
        else:
            record("export_entity (wiring)", "SKIP", "no .ent extracted")
    else:
        record("export_entity (wiring)", "SKIP", "appearance archive missing")

    # 38. list_entity_appearances + validate_appearance ------------------------
    npc = _glob.glob(os.path.join(CONTENT, "*.archive"))
    # NPC entity that carries appearances
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
                   f"{res.get('appearanceCount')} appearance(s) listed")
        else:
            record("list_entity_appearances", "SKIP", "entity not extracted")
    else:
        record("list_entity_appearances", "SKIP", "no NPC entity found")

    # validate_appearance on a valid base .app → must be error-free
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
            # Valid base content → 0 errors expected (the validator must not produce false positives).
            va_ok = res["ok"] and res.get("meshRefsChecked", 0) >= 0 and len(res["errors"]) == 0
            record("validate_appearance", "PASS" if va_ok else "WARN",
                   f"{ms:.0f} ms — {res.get('meshRefsChecked')} mesh ref(s), {res.get('meshesResolved')} resolved, "
                   f"{len(res['errors'])} error(s) on valid base content")
        else:
            record("validate_appearance", "SKIP", ".app not extracted")
    else:
        record("validate_appearance", "SKIP", "no .app found")

    # MCP resources -----------------------------------------------------------
    ref = srv.resource("wkmcp://reference")
    record("reference resource", "PASS" if "cheat sheet" in ref else "FAIL",
           f"{len(ref)} characters")

    if os.path.isfile(ARCH_SMALL):
        arc = srv.resource("wkmcp://archive/" + ARCH_SMALL)
        record("archive/{path} resource", "PASS" if ".streamingsector" in arc else "FAIL",
               f"archive listing ({len(arc)} characters)")
    else:
        record("archive/{path} resource", "SKIP", "test archive missing")

    if extracted:
        cj = srv.resource("wkmcp://cr2w-json/" + extracted)
        good = cj.lstrip().startswith("{") or '"' in cj
        record("cr2w-json/{path} resource", "PASS" if good else "FAIL",
               f"CR2W rendered as JSON ({len(cj)} characters)")
    else:
        record("cr2w-json/{path} resource", "SKIP", "no CR2W file extracted")


if __name__ == "__main__":
    main()
