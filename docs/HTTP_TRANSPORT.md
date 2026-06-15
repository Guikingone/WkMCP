# Transport HTTP (accès distant)

Par défaut, `WolvenKitMcp` parle **stdio** (pour Claude Desktop/Code en local). Le **même
binaire** peut aussi servir le MCP en **HTTP/Streamable** (pour un accès distant ou un client
web), via `ModelContextProtocol.AspNetCore`. C'est **opt-in** et **sécurisé par défaut**.

## Choix du transport (variable d'env)

| Variable | Défaut | Rôle |
|---|---|---|
| `WOLVENKIT_MCP_TRANSPORT` | `stdio` | `stdio` ou `http`. |
| `WOLVENKIT_MCP_HTTP_URL` | `http://127.0.0.1:3001` | Adresse de bind du serveur HTTP. |
| `WOLVENKIT_MCP_HTTP_TOKEN` | — | Bearer token exigé sur les requêtes (fortement recommandé). |

Endpoint MCP : **`/`** (Streamable HTTP, mode *stateless*). Le daemon WolvenKit, les 123 outils
et le pont live `CetBridge` sont identiques en stdio et en HTTP.

## Lancer en HTTP (local)

```powershell
$env:WOLVENKIT_MCP_TRANSPORT = "http"
$env:WOLVENKIT_MCP_HTTP_URL   = "http://127.0.0.1:3001"
$env:WOLVENKIT_MCP_HTTP_TOKEN = "un-secret-long-et-aléatoire"
dotnet src\WolvenKitMcp\bin\Release\net8.0\WolvenKitMcp.dll
```

Brancher Claude Code dessus :

```bash
claude mcp add --transport http wolvenkit http://127.0.0.1:3001 \
  --header "Authorization: Bearer un-secret-long-et-aléatoire"
```

Vérifier au curl (handshake `initialize`) :

```bash
# sans token -> 401
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://127.0.0.1:3001/ \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}'
# avec token -> 200 + serverInfo
curl ... -H "Authorization: Bearer un-secret-long-et-aléatoire" ...
```

## Sécurité — à lire avant d'exposer (sans détour)

Ce serveur **écrit des fichiers de jeu, installe/désinstalle des mods, et exécute du Lua
arbitraire dans le jeu vivant** (`live_execute_lua`, etc.). L'exposer en réseau = **surface RCE**
sur la machine **et** le jeu. Règles appliquées par le serveur :

- **Bind `127.0.0.1` par défaut.** L'exposition réseau est un choix explicite.
- **Bearer token** (`WOLVENKIT_MCP_HTTP_TOKEN`) : middleware → `401` sans `Authorization: Bearer`.
- **Fail-closed** : bind **non-loopback** (ex. `0.0.0.0`) **sans** token → le serveur **refuse de
  démarrer**. (Loopback sans token = autorisé pour le dev, mais averti.)
- **TLS = non géré en interne.** Pour le distant, place un **reverse proxy TLS** devant.

### Accès distant recommandé : reverse proxy TLS, serveur en loopback

Garde le serveur sur `127.0.0.1` et laisse le proxy terminer le TLS et router. Exemple Caddy :

```
wolvenkit.example.com {
    reverse_proxy 127.0.0.1:3001
}
```

⚠ **Important** : derrière un proxy, le serveur bind en loopback → le *fail-closed* ne **force**
pas le token. **Définis quand même `WOLVENKIT_MCP_HTTP_TOKEN`** : sinon le proxy expose un MCP
non authentifié à Internet. Limite aussi l'accès réseau (firewall/ACL/VPN) tant que possible.

## Notes

- Le bundle `.mcpb` (extension desktop) reste **stdio**. Le mode HTTP se lance à la main ou en
  service (NSSM/Windows Service, systemd, supervisor) avec les variables d'env ci-dessus.
- Mode **stateless** : pas de session côté serveur (le serveur ne pousse pas de notifications) —
  simple et compatible reverse proxy / load-balancer.
- OAuth : le SDK fournit aussi un handler OAuth (Protected Resource Metadata) si un fournisseur
  d'identité entre en jeu ; le bearer token suffit pour un self-host.
