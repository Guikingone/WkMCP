# HTTP transport (remote access)

By default, `WkMcp` speaks **stdio** (for local Claude Desktop/Code). The **same
binary** can also serve MCP over **HTTP/Streamable** (for remote access or a web
client), via `ModelContextProtocol.AspNetCore`. This is **opt-in** and **secure by default**.

## Why HTTP vs stdio?

`stdio` is the right default for a local Claude Desktop / Claude Code session —
the client launches the server as a child process and talks to it over its
standard streams. Reach for **HTTP/Streamable** only when stdio does not fit:

- **Remote or shared access** — a server on one machine, an agent (or several)
  on another, over the network.
- **A web client** that cannot spawn a local process.
- **Multiple concurrent clients** against one daemon (stateless mode fans out).

HTTP adds a network surface the server does not have in stdio, so it is opt-in and
locked down by default — loopback bind, bearer token, and a fail-closed start check
(see [Security](#security--read-before-exposing)).

## Transport selection (env variable)

| Variable | Default | Role |
|---|---|---|
| `WKMCP_TRANSPORT` | `stdio` | `stdio` or `http`. |
| `WKMCP_HTTP_URL` | `http://127.0.0.1:3001` | Bind address of the HTTP server. |
| `WKMCP_HTTP_TOKEN` | — | Bearer token required on requests (strongly recommended). |

MCP endpoint: **`/`** (Streamable HTTP, *stateless* mode). The WolvenKit daemon, the 123 tools
and the `CetBridge` live bridge are identical in stdio and in HTTP.

## Launch over HTTP (local)

```powershell
$env:WKMCP_TRANSPORT = "http"
$env:WKMCP_HTTP_URL   = "http://127.0.0.1:3001"
$env:WKMCP_HTTP_TOKEN = "a-long-random-secret"
dotnet src\WkMcp\bin\Release\net8.0\WkMcp.dll
```

Connect Claude Code to it:

```bash
claude mcp add --transport http wolvenkit http://127.0.0.1:3001 \
  --header "Authorization: Bearer a-long-random-secret"
```

Verify with curl. First the `initialize` handshake — **without** a token you get `401`:

```bash
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://127.0.0.1:3001/ \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}'
# -> 401
```

With the token, the same request returns `200` and the server info (Streamable HTTP
replies as an SSE event stream; `initialize` is small enough to read inline):

```bash
curl -s -X POST http://127.0.0.1:3001/ \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Authorization: Bearer a-long-random-secret" \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}'
# -> 200  +  event: message ... {"result":{"serverInfo":{"name":"WkMcp", ...}, ...}}
```

## Security — read before exposing

This server **writes game files, installs/uninstalls mods, and executes arbitrary Lua
in the live game** (`live_execute_lua`, etc.). Exposing it on the network = **RCE surface**
on the machine **and** the game. Rules enforced by the server:

- **Bind `127.0.0.1` by default.** Network exposure is an explicit choice.
- **Bearer token** (`WKMCP_HTTP_TOKEN`): middleware → `401` without `Authorization: Bearer`.
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
force the token. **Set `WKMCP_HTTP_TOKEN` anyway**: otherwise the proxy exposes an
unauthenticated MCP to the Internet. Also restrict network access (firewall/ACL/VPN) as much as possible.

## Troubleshooting

- **`Refusing to start: non-loopback bind without a token`** — you set
  `WKMCP_HTTP_URL` to a non-loopback address (e.g. `0.0.0.0`) without
  `WKMCP_HTTP_TOKEN`. Either bind loopback, or set a token.
- **`401 Unauthorized`** — the `Authorization: Bearer <token>` header is missing or
  does not match `WKMCP_HTTP_TOKEN` exactly.
- **`Address already in use` / port in use** — another process holds the port (a
  previous server instance, or another tool). Change `WKMCP_HTTP_URL`, or
  stop the other process.
- **Behind a reverse proxy, requests hang** — confirm the proxy forwards to the
  loopback address the server actually binds to, and passes the
  `Accept: application/json, text/event-stream` header through (SSE needs it).
- **Works on loopback, fails remotely** — the server is bound to `127.0.0.1`, so a
  remote host cannot reach it directly. That is intentional: terminate TLS at a
  proxy (see above) rather than rebinding to `0.0.0.0`.

## Notes

- The `.mcpb` bundle (desktop extension) stays **stdio**. HTTP mode is launched by
  hand or as a service with the env variables above.
- **Running as a service** — on Windows, [NSSM](https://nssm.cc) wraps the DLL
  launch as a Windows Service (`nssm install WkMcp dotnet.exe
  src\WkMcp\bin\Release\net8.0\WkMcp.dll`, then set the three
  `WKMCP_*` env variables in the service environment); on Linux, a
  `systemd` unit with `Environment=` lines for the same variables.
- **Stateless** mode: no server-side session (the server pushes no notifications) —
  simple and compatible with a reverse proxy / load balancer.
- The SDK also ships an OAuth (Protected Resource Metadata) handler for setups with
  an identity provider; a bearer token is enough for self-hosting.
