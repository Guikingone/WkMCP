# Pont live in-game (CETBridge)

Les 88 outils « classiques » de WolvenKit MCP sont **hors-ligne** : ils opèrent sur des
fichiers et des archives, jeu éteint. Les outils **`live_*`** font l'inverse : ils pilotent
un Cyberpunk 2077 **en cours d'exécution** — exécuter du Lua, lire/écrire l'état du jeu,
spawn, téléportation, météo, TweakDB en mémoire vive, observation d'événements.

> Préfixe `live_` = mémoire vive du jeu. Sans préfixe = fichiers. Ex. `tweakdb_query`
> (offline, lit `tweakdb.bin`) vs `live_tweakdb_get` (lit la DB du jeu en cours).

## Comment ça marche

On ne peut pas injecter dans la VM Lua du jeu depuis l'extérieur : il faut un **mod
in-game**. Le serveur MCP parle à un petit mod Lua (**CETBridge**, chargé par Cyber Engine
Tweaks) qui exécute les commandes dans le moteur et renvoie le résultat.

```
Claude ─MCP/JSON-RPC─▶ WolvenKitMcp ──TCP 127.0.0.1:27010──▶ mod CETBridge (Lua/CET) ─▶ jeu
                          (CetBridge.cs)  └─repli fichier──▶  (command.json / response.json)
```

Deux transports, bascule automatique :

| Transport | Latence | Dépendance | Détail |
|---|---|---|---|
| **TCP** (recommandé) | ~1 ms | RedSocket | Le serveur écoute sur `127.0.0.1:27010` ; le mod s'y connecte. |
| **Fichier** (repli) | ~16-33 ms | aucune | Le serveur écrit `command.json`, le mod répond via `response.json`. |

Le serveur tente TCP ; si le port est pris (une autre session) ou si RedSocket est absent,
il retombe sur le transport fichier. Le mod, lui, écoute **toujours** les deux.

## Prérequis

- **Cyberpunk 2077** lancé (le live agit sur le jeu en cours).
- **Cyber Engine Tweaks (CET)** 1.32+ — charge le mod Lua.
- **RED4ext** 1.25+.
- **RedSocket** (optionnel mais recommandé, pour le TCP ~1 ms). Sans lui : transport fichier.

## Installation

1. **Copier le mod** `live-bridge/CETBridge/` (inclus dans le dépôt et dans le bundle `.mcpb`)
   dans le dossier des mods CET :
   ```
   <jeu>/bin/x64/plugins/cyber_engine_tweaks/mods/CETBridge/
   ```
2. *(Optionnel, pour le TCP)* installer **RedSocket** (plugin RED4ext).
3. **Lancer le jeu** (ou via l'outil offline `launch_game`).
4. Vérifier depuis Claude : **`live_status`** → doit indiquer `connected: true` et le transport.

Le serveur MCP n'a **aucune** dépendance supplémentaire (pur réseau + JSON, dans le process
serveur ; le daemon WolvenKit n'est pas concerné).

## Configuration (variables d'environnement, toutes optionnelles)

| Variable | Défaut | Rôle |
|---|---|---|
| `CET_TRANSPORT` | `tcp` | `tcp` ou `file` (force le repli, n'ouvre pas le port). |
| `CET_TCP_PORT` | `27010` | Port du listener TCP. |
| `CET_BRIDGE_DIR` | — | Dossier du mod CETBridge (sinon dérivé du `gamePath` passé aux outils). |
| `CET_BRIDGE_TIMEOUT_MS` | `5000` | Délai max d'une requête. |

Le paramètre `gamePath` des outils sert au repli fichier (localiser le mod) ; il est inutile
en TCP. En TCP pur, on peut donc l'omettre.

## Outils (35)

### Fondation (débloque tout)
| Outil | Rôle |
|---|---|
| `live_status` | Connectivité du pont (transport, heartbeat, dossier). Marche jeu éteint. |
| `live_execute_lua` | Exécute du Lua (effets de bord ; sortie `print()` capturée). |
| `live_eval` | Évalue une expression Lua et renvoie sa valeur (types CET sérialisés). |
| `live_batch` | Plusieurs instructions Lua en un aller-retour. |

### Lecture d'état
`live_player_info`, `live_game_state`, `live_inventory`, `live_equipped`,
`live_active_effects`, `live_appearance`, `live_vehicles`, `live_nearby_entities`,
`live_scanner`.

### Mutation joueur & monde
`live_add_item`, `live_remove_item`, `live_teleport`, `live_set_stat`, `live_apply_effect`,
`live_remove_effect`, `live_god_mode`, `live_set_level`, `live_spawn_vehicle`,
`live_set_time`, `live_set_weather`, `live_kill_nearby`, `live_notify`, `live_play_sound`.

### TweakDB en mémoire vive + RTTI
`live_tweakdb_get`, `live_tweakdb_set`, `live_dump_type`, `live_tweakdb_search`.

### Quêtes & événements
`live_get_quest_fact`, `live_set_quest_fact`, `live_observe`, `live_observations`.

Tout passe par les mêmes 3 verbes de protocole (`exec`/`eval`/`query`) : les outils de 1re
classe ci-dessus ne sont que des raccourcis ergonomiques au-dessus de handlers Lua nommés.
N'importe quoi d'autre est faisable via `live_execute_lua` / `live_eval`.

## Sécurité & limites (sans détour)

- **Exécuter du Lua dans le jeu vivant est puissant et risqué** : une boucle infinie peut
  **figer** le jeu. Côté Lua chaque exécution est protégée par `pcall` ; côté serveur un
  **timeout** (5 s) protège l'agent — **pas** le jeu.
- Le listener TCP est restreint à **127.0.0.1** (pas de réseau public, pas d'auth — outil de
  développement local).
- Les écritures `live_tweakdb_set` persistent **jusqu'au redémarrage** du jeu.
- **Non testable en CI** : seule la couche protocole l'est (cf. `CetBridgeProtocolTests`). Le
  bout-en-bout exige le jeu lancé — voir `test-live-bridge.py`.

## Dépannage

`live_status` renvoie `connected: false` :
- Le jeu tourne-t-il, hors menu de chargement ?
- Le mod est-il dans `…/cyber_engine_tweaks/mods/CETBridge/` ? (vérifier l'overlay CET → mod chargé)
- En TCP : RedSocket est-il installé ? Sinon forcer `CET_TRANSPORT=file` (+ `gamePath` ou `CET_BRIDGE_DIR`).
- Port 27010 déjà pris (autre session / cyber-engine-tweak-mcp) → le serveur bascule en fichier ;
  vérifier `bridgeDir` dans la sortie de `live_status`.

## Provenance & licence

Le mod Lua `CETBridge/` est repris tel quel du projet **Y4rd13/cyber-engine-tweak-mcp**
(licence **MIT**, voir `live-bridge/CETBridge/LICENSE.upstream` ; `json.lua` = rxi, MIT). Le
protocole de fil (trames JSON `\r\n`, `exec`/`eval`/`query`, repli fichier) est volontairement
identique pour réutiliser ce mod sans le modifier.
