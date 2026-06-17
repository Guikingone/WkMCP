# HTTP transport (remote access)

By default, `WolvenKitMcp` speaks **stdio** (for local Claude Desktop/Code). The **same
binary** can also serve MCP over **HTTP/Streamable** (for remote access or a web
client), via `ModelContextProtocol.AspNetCore`. This is **opt-in** and **secure by default**.

## Transport selection (env variable)

| Variable | Default | Role |
|---|---|---|
| `WOLVENKIT_MCP_TRANSPORT` | `stdio` | `stdio` or `http`. |
| `WOLVENKIT_MCP_HTTP_URL` | `http://127.0.0.1:3001` | Bind address of the HTTP server. |
| `WOLVENKIT_MCP_HTTP_TOKEN` | — | Bearer token required on requests (strongly recommended). |

MCP endpoint: **`/`** (Streamable HTTP, *stateless* mode). The WolvenKit daemon, the 123 tools
and the `CetBridge` live bridge are identical in stdio and in HTTP.

## Launch over HTTP (local)

```powershell
$env:WOLVENKIT_MCP_TRANSPORT = "http"
$env:WOLVENKIT_MCP_HTTP_URL   = "http://127.0.0.1:3001"
$env:WOLVENKIT_MCP_HTTP_TOKEN = "a-long-random-secret"
dotnet src\WolvenKitMcp\bin\Release\net8.0\WolvenKitMcp.dll
```

Connect Claude Code to it:

```bash
claude mcp add --transport http wolvenkit http://127.0.0.1:3001 \
  --header "Authorization: Bearer a-long-random-secret"
```

Verify with curl (`initialize` handshake):

```bash
# without token -> 401
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://127.0.0.1:3001/ \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}'
# with token -> 200 + serverInfo
curl ... -H "Authorization: Bearer a-long-random-secret" ...
```

## Security — read before exposing (no beating around the bush)

This server **writes game files, installs/uninstalls mods, and executes arbitrary Lua
in the live game** (`live_execute_lua`, etc.). Exposing it on the network = **RCE surface**
on the machine **and** the game. Rules enforced by the server:

- **Bind `127.0.0.1` by default.** Network exposure is an explicit choice.
- **Bearer token** (`WOLVENKIT_MCP_HTTP_TOKEN`): middleware → `401` without `Authorization: Bearer`.
- **Fail-closed**: a **non-loopback** bind (e.g. `0.0.0.0`) **without** a token → the server **refuses
  to start**. (Loopback without a token = allowed for dev, but with a warning.)
- **TLS = not handled internally.** For remote access, place a **TLS reverse proxy** in front.

### Recommended remote access: TLS reverse proxy, server on loopback

Keep the server on `127.0.0.1` and let the proxy terminate TLS and route. Caddy example:

```
wolvenkit.example.com {
    reverse_proxy 127.0.0.1:3001
}
```

⚠ **Important**: behind a proxy, the server binds in loopback → the *fail-closed* does **not**
force the token. **Set `WOLVENKIT_MCP_HTTP_TOKEN` anyway**: otherwise the proxy exposes an
unauthenticated MCP to the Internet. Also restrict network access (firewall/ACL/VPN) as much as possible.

## Notes

- The `.mcpb` bundle (desktop extension) stays **stdio**. HTTP mode is launched by hand or as a
  service (NSSM/Windows Service, systemd, supervisor) with the env variables above.
- **Stateless** mode: no server-side session (the server pushes no notifications) —
  simple and compatible with a reverse proxy / load balancer.
- OAuth: the SDK also provides an OAuth handler (Protected Resource Metadata) if an identity
  provider comes into play; the bearer token is enough for self-hosting.
