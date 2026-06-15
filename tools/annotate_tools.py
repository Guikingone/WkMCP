# -*- coding: utf-8 -*-
"""Applique les tool annotations MCP (ReadOnly/Destructive/Idempotent) aux 120 outils.

Usage : python tools/annotate_tools.py
Réécrit chaque ligne `[McpServerTool(Name = "x")]` avec les hints de la table
ci-dessous. Idempotent : une ligne déjà annotée n'est pas retouchée.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "src" / "WolvenKitMcp"

# name: (ReadOnly, Destructive, Idempotent)
# ReadOnly    : ne modifie ni les fichiers utilisateur/jeu ni l'état du jeu
#               (écrire uniquement dans un dossier temp compte comme lecture seule).
# Destructive : peut écraser/supprimer des données existantes (fichiers du jeu,
#               état de la partie en cours) de façon non triviale à annuler.
# Idempotent  : un second appel identique n'a pas d'effet supplémentaire.
TABLE = {
    # ── WolvenKitTools ──────────────────────────────────────────────
    "wolvenkit_status": (True, False, True),
    "extract_localization": (False, False, True),
    "build_localization": (False, False, True),
    "clear_cache": (False, False, True),
    "compute_hash": (True, False, True),
    "resolve_hash": (True, False, True),
    "tweakdb_resolve": (True, False, True),
    "tweakdb_query": (True, False, True),
    "archive_info": (True, False, True),
    "find_in_archives": (True, False, True),
    "diff_archives": (True, False, True),
    "extract_files": (False, False, True),
    "uncook": (False, False, True),
    "cr2w_to_json": (False, False, True),
    "json_to_cr2w": (False, False, True),
    "export_files": (False, False, True),
    "export_animation": (False, False, True),
    "export_morphtarget": (False, False, True),
    "export_mlmask": (False, False, True),
    "export_entity": (False, False, True),
    "export_materials": (False, False, True),
    "read_game_file": (True, False, True),
    "write_game_file": (False, True, True),
    "wwise_export": (False, False, True),
    "extract_audio": (False, False, True),
    "import_audio": (False, False, True),
    "loc_resolve": (True, False, True),
    "oodle_compress": (False, False, True),
    "oodle_decompress": (False, False, True),
    "pack_archive": (False, False, True),
    "import_raw": (False, False, True),
    "build_project": (False, False, True),
    "detect_conflicts": (True, False, True),
    "list_installed_mods": (True, False, True),
    "create_mod_project": (False, False, False),
    "generate_modproj": (False, False, True),
    "inspect_mesh": (True, False, True),
    "inspect_texture": (True, False, True),
    "describe_tweak_record": (True, False, True),
    "read_tweak": (True, False, True),
    "write_tweak": (False, True, True),
    "validate_tweak": (True, False, True),
    "generate_redscript_template": (False, False, True),
    "generate_tweak_template": (False, False, True),
    "install_tweak": (False, True, True),
    "read_script": (True, False, True),
    "lint_script": (True, False, True),
    "create_redmod_project": (False, False, False),
    "pack_redmod": (False, False, True),
    "install_redmod": (False, True, True),
    "backup_mods": (False, False, False),
    "restore_mods": (False, True, True),
    "lint_mod": (True, False, True),
    "mod_summary": (True, False, True),
    "dump_records": (False, False, True),
    "launch_game": (False, False, False),
    "tail_game_logs": (True, False, True),
    "uninstall_mod": (False, True, True),
    "uninstall_redmod": (False, True, True),
    "uninstall_tweak": (False, True, True),
    "deploy_redmod": (False, False, True),
    "install_mod": (False, True, True),
    # ── ModdingTools ────────────────────────────────────────────────
    "analyze_dependencies": (True, False, True),
    "check_requirements": (True, False, True),
    "mod_doctor": (True, False, True),
    "validate_xl": (True, False, True),
    "scaffold_archivexl": (False, False, False),
    "find_references": (True, False, True),
    "diff_mod_vs_base": (True, False, True),
    "scaffold_mod": (False, False, False),
    "package_mod": (False, False, True),
    "inspect_journal": (True, False, True),
    "find_journal_entry": (True, False, True),
    "inspect_cr2w": (True, False, True),
    "find_in_cr2w": (True, False, True),
    "diagnose_logs": (True, False, True),
    "analyze_conflicts": (True, False, True),
    "validate_item_mod": (True, False, True),
    "lint_tweak": (True, False, True),
    "generate_manifest": (False, False, True),
    "resolve_dynamic_appearance": (True, False, True),
    "migration_check": (True, False, True),
    "toggle_mods": (False, False, False),
    "list_entity_appearances": (True, False, True),
    "validate_appearance": (True, False, True),
    # ── LiveTools (jeu en cours d'exécution) ────────────────────────
    "live_status": (True, False, True),
    "live_execute_lua": (False, True, False),
    "live_eval": (False, False, False),
    "live_batch": (False, True, False),
    "live_player_info": (True, False, True),
    "live_game_state": (True, False, True),
    "live_inventory": (True, False, True),
    "live_equipped": (True, False, True),
    "live_active_effects": (True, False, True),
    "live_appearance": (True, False, True),
    "live_vehicles": (True, False, True),
    "live_nearby_entities": (True, False, True),
    "live_scanner": (True, False, True),
    "live_add_item": (False, False, False),
    "live_remove_item": (False, True, False),
    "live_teleport": (False, False, True),
    "live_set_stat": (False, True, True),
    "live_apply_effect": (False, False, False),
    "live_remove_effect": (False, False, True),
    "live_god_mode": (False, False, True),
    "live_set_level": (False, True, True),
    "live_spawn_vehicle": (False, False, False),
    "live_set_time": (False, False, True),
    "live_set_weather": (False, False, True),
    "live_kill_nearby": (False, True, False),
    "live_notify": (False, False, False),
    "live_play_sound": (False, False, False),
    "live_tweakdb_get": (True, False, True),
    "live_tweakdb_set": (False, True, True),
    "live_dump_type": (True, False, True),
    "live_tweakdb_search": (True, False, True),
    "live_get_quest_fact": (True, False, True),
    "live_set_quest_fact": (False, True, True),
    "live_observe": (False, False, False),
    "live_observations": (True, False, True),
}

PATTERN = re.compile(r'\[McpServerTool\(Name = "([a-z0-9_]+)"\)\]')


def cs(b: bool) -> str:
    return "true" if b else "false"


def main() -> int:
    seen = set()
    for path in ROOT.glob("*.cs"):
        text = path.read_text(encoding="utf-8")

        def repl(m: re.Match) -> str:
            name = m.group(1)
            if name not in TABLE:
                print(f"!! outil sans classification : {name} ({path.name})")
                return m.group(0)
            seen.add(name)
            ro, dest, idem = TABLE[name]
            return (f'[McpServerTool(Name = "{name}", ReadOnly = {cs(ro)}, '
                    f'Destructive = {cs(dest)}, Idempotent = {cs(idem)})]')

        new = PATTERN.sub(repl, text)
        if new != text:
            path.write_text(new, encoding="utf-8")
            print(f"annoté : {path.name}")

    missing = set(TABLE) - seen
    if missing:
        print(f"!! classifications sans outil correspondant : {sorted(missing)}")
        return 1
    print(f"OK : {len(seen)}/{len(TABLE)} outils annotés")
    return 0


if __name__ == "__main__":
    sys.exit(main())
