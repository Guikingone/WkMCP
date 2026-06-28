using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WkMcp;

// MCP server for WolvenKit. Transport chosen at startup via WKMCP_TRANSPORT:
//   stdio (default) — local Claude Desktop/Code; stdout reserved for JSON-RPC, logs on stderr.
//   http           — HTTP/Streamable server (ModelContextProtocol.AspNetCore), secure by
//                    default (loopback bind + bearer token + fail-closed). See docs/HTTP_TRANSPORT.md.
// The 152 tools (including the 36 live_*), the daemon and the CetBridge bridge are identical regardless
// of the transport — only the host construction differs.

var transport = (Cp77ToolsRunner.EnvOrLegacy(HttpBridgeSecurity.TransportEnv, HttpBridgeSecurity.LegacyTransportEnv) ?? "stdio")
    .Trim().ToLowerInvariant();

// Pre-warms the WolvenKit daemon (HashService ~6 s) at startup, regardless of the transport.
_ = Task.Run(() => Cp77ToolsRunner.Shared.RunAsync(new[] { "--version" }, CancellationToken.None));

// Purges temp folders from previous sessions (> 24 h) — best-effort, off the critical path.
_ = Task.Run(Cp77ToolsRunner.PurgeStaleTempDirs);

// Registration common to both transports: DI + tools/resources/prompts by reflection.
static void RegisterMcp(IServiceCollection services, bool http)
{
    // A single shared instance of the runner → a single WolvenKit daemon.
    services.AddSingleton<Cp77ToolsRunner>(_ => Cp77ToolsRunner.Shared);
    // "Live" bridge to the running game (CETBridge mod). Injected into the live_* tools.
    services.AddSingleton<CetBridge>();

    var mcp = services.AddMcpServer();
    if (http) mcp.WithHttpTransport(options => options.Stateless = true);
    else mcp.WithStdioServerTransport();
    mcp.WithToolsFromAssembly()
       .WithResourcesFromAssembly()
       .WithPromptsFromAssembly();
}

if (transport == "http")
{
    var url = Cp77ToolsRunner.EnvOrLegacy(HttpBridgeSecurity.UrlEnv, HttpBridgeSecurity.LegacyUrlEnv);
    if (string.IsNullOrWhiteSpace(url)) url = HttpBridgeSecurity.DefaultUrl;
    var token = Cp77ToolsRunner.EnvOrLegacy(HttpBridgeSecurity.TokenEnv, HttpBridgeSecurity.LegacyTokenEnv);

    // Fail-closed: no exposure outside loopback without a token.
    var (okStart, error) = HttpBridgeSecurity.CheckStartup(url, token);
    if (!okStart)
    {
        Console.Error.WriteLine("[WkMcp] " + error);
        return 2;
    }

    var builder = WebApplication.CreateBuilder(args);
    RegisterMcp(builder.Services, http: true);

    var app = builder.Build();
    // Boots the live bridge's TCP listener (idempotent) — coexists with the HTTP server.
    app.Services.GetRequiredService<CetBridge>().EnsureStarted();
    app.UseWolvenKitOriginGuard(url);  // always-on DNS-rebinding guard (Host/Origin)
    app.UseWolvenKitBearerAuth(token); // no-op if no token (dev loopback)
    app.MapMcp();

    if (string.IsNullOrWhiteSpace(token))
        app.Logger.LogWarning("[WkMcp] MCP HTTP on {Url} WITHOUT auth (loopback). Do not expose " +
            "this port; set {Env} to enable the bearer token.", url, HttpBridgeSecurity.TokenEnv);
    else
        app.Logger.LogInformation("[WkMcp] MCP HTTP on {Url} (bearer auth enabled).", url);

    await app.RunAsync(url);
    return 0;
}
else
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    RegisterMcp(builder.Services, http: false);

    var app = builder.Build();
    // Boots the live bridge's TCP listener (idempotent) so CETBridge can connect.
    app.Services.GetRequiredService<CetBridge>().EnsureStarted();
    await app.RunAsync();
    return 0;
}
