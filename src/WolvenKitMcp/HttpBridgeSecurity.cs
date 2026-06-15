using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace WolvenKitMcp;

/// <summary>
/// HTTP mode safeguards (opt-in). The server writes game files, installs mods
/// and runs Lua in the live game (<c>live_*</c> tools): exposing it on the network is an
/// RCE surface. Hence: <b>loopback bind by default</b> + <b>bearer token</b> + <b>fail-closed</b>
/// (refuses to start if bind is non-loopback without a token). TLS = via reverse proxy.
///
/// The <see cref="IsLoopback"/> / <see cref="CheckStartup"/> / <see cref="TokenEquals"/> helpers
/// are pure and tested (see HttpBridgeSecurityTests).
/// </summary>
internal static class HttpBridgeSecurity
{
    internal const string TransportEnv = "WOLVENKIT_MCP_TRANSPORT"; // stdio | http
    internal const string UrlEnv = "WOLVENKIT_MCP_HTTP_URL";        // ex. http://127.0.0.1:3001
    internal const string TokenEnv = "WOLVENKIT_MCP_HTTP_TOKEN";    // bearer token (recommended)
    internal const string DefaultUrl = "http://127.0.0.1:3001";

    /// <summary>True if the URL binds to a loopback interface (127.0.0.0/8, ::1, localhost).
    /// Everything else (0.0.0.0, *, +, public IP, DNS name) is considered NON-loopback.</summary>
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

    /// <summary>Fail-closed rule applied at HTTP mode startup.</summary>
    internal static (bool ok, string? error) CheckStartup(string url, string? token)
    {
        bool hasToken = !string.IsNullOrWhiteSpace(token);
        if (!IsLoopback(url) && !hasToken)
            return (false,
                $"Refusing to start in HTTP: non-loopback bind ({url}) WITHOUT {TokenEnv}. " +
                "Set a token (and put TLS in front, e.g. a reverse proxy), or bind to 127.0.0.1.");
        return (true, null);
    }

    /// <summary>Constant-time comparison (via SHA-256, fixed length) of two tokens.</summary>
    internal static bool TokenEquals(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
        Span<byte> hp = stackalloc byte[32];
        Span<byte> he = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), hp);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), he);
        return CryptographicOperations.FixedTimeEquals(hp, he);
    }

    /// <summary>Bearer middleware. Without a configured token: no-op (dev loopback). With a token:
    /// requires <c>Authorization: Bearer &lt;token&gt;</c>, otherwise 401.</summary>
    internal static void UseWolvenKitBearerAuth(this WebApplication app, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return; // no auth configured

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
                await context.Response.WriteAsync("401 Unauthorized — bearer token required.");
                return;
            }
            await next();
        });
    }
}
