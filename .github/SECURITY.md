# Security policy

## Supported versions

WolvenKit MCP is a young project. Security fixes are released for the latest
tagged version only.

## Reporting a vulnerability

Please **do not** open a public GitHub issue for a security problem. Instead,
report it privately via GitHub's "Report a vulnerability" flow
(https://github.com/Guikingone/WolvenkitMCP/security/advisories/new) or contact
the maintainer directly. Include a description, reproduction steps, and the
version affected. We will acknowledge receipt promptly and coordinate a fix and
disclosure.

## Attack surface to keep in mind

This server is a powerful local automation tool. The following are
security-relevant:

- **Subprocess execution.** The server drives the WolvenKit CLI (`cp77tools`)
  and the daemon as local processes; tools that take a path can write files in
  the game folders.
- **HTTP transport.** When `WOLVENKIT_MCP_TRANSPORT=http` is set, the server
  exposes MCP over HTTP. It binds loopback by default and requires a bearer
  token; a non-loopback bind without a token makes the server refuse to start
  (fail-closed). See `docs/HTTP_TRANSPORT.md`. Remote exposure equals remote code
  execution on the host **and** the game.
- **Live bridge.** The `live_*` tools execute arbitrary Lua in a running game
  (TCP `127.0.0.1:27010` only, no auth) and can mutate game state. See
  `docs/LIVE_BRIDGE.md`.

When hardening a deployment: keep the HTTP transport on loopback behind a TLS
reverse proxy, always set `WOLVENKIT_MCP_HTTP_TOKEN`, and never expose the live
bridge beyond the local machine.