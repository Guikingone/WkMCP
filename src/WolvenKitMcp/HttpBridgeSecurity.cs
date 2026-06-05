using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace WolvenKitMcp;

/// <summary>
/// Garde-fous du mode HTTP (opt-in). Le serveur écrit des fichiers de jeu, installe des mods
/// et exécute du Lua dans le jeu vivant (outils <c>live_*</c>) : l'exposer en réseau est une
/// surface RCE. D'où : <b>bind loopback par défaut</b> + <b>bearer token</b> + <b>fail-closed</b>
/// (refus de démarrer si bind non-loopback sans token). TLS = via reverse proxy.
///
/// Les helpers <see cref="IsLoopback"/> / <see cref="CheckStartup"/> / <see cref="TokenEquals"/>
/// sont purs et testés (cf. HttpBridgeSecurityTests).
/// </summary>
internal static class HttpBridgeSecurity
{
    internal const string TransportEnv = "WOLVENKIT_MCP_TRANSPORT"; // stdio | http
    internal const string UrlEnv = "WOLVENKIT_MCP_HTTP_URL";        // ex. http://127.0.0.1:3001
    internal const string TokenEnv = "WOLVENKIT_MCP_HTTP_TOKEN";    // bearer token (recommandé)
    internal const string DefaultUrl = "http://127.0.0.1:3001";

    /// <summary>Vrai si l'URL bind sur une interface loopback (127.0.0.0/8, ::1, localhost).
    /// Tout le reste (0.0.0.0, *, +, IP publique, nom DNS) est considéré NON-loopback.</summary>
    internal static bool IsLoopback(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            !Uri.TryCreate("http://" + url, UriKind.Absolute, out uri))
            return false;

        var host = uri!.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>Règle fail-closed appliquée au démarrage du mode HTTP.</summary>
    internal static (bool ok, string? error) CheckStartup(string url, string? token)
    {
        bool hasToken = !string.IsNullOrWhiteSpace(token);
        if (!IsLoopback(url) && !hasToken)
            return (false,
                $"Refus de démarrer en HTTP : bind non-loopback ({url}) SANS {TokenEnv}. " +
                "Définis un token (et place du TLS devant, ex. reverse proxy), ou bind sur 127.0.0.1.");
        return (true, null);
    }

    /// <summary>Comparaison à temps constant (via SHA-256, longueur fixe) de deux tokens.</summary>
    internal static bool TokenEquals(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
        Span<byte> hp = stackalloc byte[32];
        Span<byte> he = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), hp);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), he);
        return CryptographicOperations.FixedTimeEquals(hp, he);
    }

    /// <summary>Middleware bearer. Sans token configuré : no-op (dev loopback). Avec token :
    /// exige <c>Authorization: Bearer &lt;token&gt;</c>, sinon 401.</summary>
    internal static void UseWolvenKitBearerAuth(this WebApplication app, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return; // pas d'auth configurée

        app.Use(async (context, next) =>
        {
            string? provided = null;
            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                provided = header[prefix.Length..].Trim();

            if (!TokenEquals(provided, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                await context.Response.WriteAsync("401 Unauthorized — bearer token requis.");
                return;
            }
            await next();
        });
    }
}
