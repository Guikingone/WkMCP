using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WolvenKitMcp;

// Serveur MCP pour WolvenKit. Transport choisi au démarrage via WOLVENKIT_MCP_TRANSPORT :
//   stdio (défaut) — Claude Desktop/Code local ; stdout réservé au JSON-RPC, logs sur stderr.
//   http           — serveur HTTP/Streamable (ModelContextProtocol.AspNetCore), sécurisé par
//                    défaut (bind loopback + bearer token + fail-closed). Cf. docs/HTTP_TRANSPORT.md.
// Les outils (120, dont les live_*), le daemon et le pont CetBridge sont identiques quel que soit
// le transport — seule la construction de l'hôte diffère.

var transport = (Environment.GetEnvironmentVariable(HttpBridgeSecurity.TransportEnv) ?? "stdio")
    .Trim().ToLowerInvariant();

// Préchauffe le daemon WolvenKit (HashService ~6 s) dès le démarrage, quel que soit le transport.
_ = Task.Run(() => Cp77ToolsRunner.Shared.RunAsync(new[] { "--version" }, CancellationToken.None));

// Enregistrement commun aux deux transports : DI + outils/ressources/prompts par réflexion.
static void RegisterMcp(IServiceCollection services, bool http)
{
    // Une seule instance partagée du runner → un seul daemon WolvenKit.
    services.AddSingleton<Cp77ToolsRunner>(_ => Cp77ToolsRunner.Shared);
    // Pont « live » vers le jeu en cours (mod CETBridge). Injecté dans les outils live_*.
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
    var url = Environment.GetEnvironmentVariable(HttpBridgeSecurity.UrlEnv);
    if (string.IsNullOrWhiteSpace(url)) url = HttpBridgeSecurity.DefaultUrl;
    var token = Environment.GetEnvironmentVariable(HttpBridgeSecurity.TokenEnv);

    // Fail-closed : pas d'exposition hors loopback sans token.
    var (okStart, error) = HttpBridgeSecurity.CheckStartup(url, token);
    if (!okStart)
    {
        Console.Error.WriteLine("[WolvenKitMcp] " + error);
        return 2;
    }

    var builder = WebApplication.CreateBuilder(args);
    RegisterMcp(builder.Services, http: true);

    var app = builder.Build();
    // Amorce le listener TCP du pont live (idempotent) — coexiste avec le serveur HTTP.
    app.Services.GetRequiredService<CetBridge>().EnsureStarted();
    app.UseWolvenKitBearerAuth(token); // no-op si aucun token (dev loopback)
    app.MapMcp();

    if (string.IsNullOrWhiteSpace(token))
        app.Logger.LogWarning("[WolvenKitMcp] MCP HTTP sur {Url} SANS auth (loopback). N'expose pas " +
            "ce port ; définis {Env} pour activer le bearer token.", url, HttpBridgeSecurity.TokenEnv);
    else
        app.Logger.LogInformation("[WolvenKitMcp] MCP HTTP sur {Url} (auth bearer activée).", url);

    await app.RunAsync(url);
    return 0;
}
else
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    RegisterMcp(builder.Services, http: false);

    var app = builder.Build();
    // Amorce le listener TCP du pont live (idempotent) pour que CETBridge puisse se connecter.
    app.Services.GetRequiredService<CetBridge>().EnsureStarted();
    await app.RunAsync();
    return 0;
}
