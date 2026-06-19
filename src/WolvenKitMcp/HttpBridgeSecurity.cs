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

    private static string Norm(string h) => h.Trim('[', ']').ToLowerInvariant();

    /// <summary>Hosts accepted in the Host/Origin headers: loopback aliases plus the bound host.</summary>
    internal static HashSet<string> AllowedHosts(string url)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1" };
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            Uri.TryCreate("http://" + url, UriKind.Absolute, out u))
            set.Add(Norm(u!.Host));
        return set;
    }

    /// <summary>Host header must be empty (some local clients omit it) or in the allowlist.
    /// A DNS-rebinding request carries the attacker's domain as Host, which is not allowed.</summary>
    internal static bool IsHostAllowed(string? host, ISet<string> allowed)
        => string.IsNullOrEmpty(host) || allowed.Contains(Norm(host));

    /// <summary>Origin must be absent (non-browser client like Claude Desktop) or in the allowlist.
    /// A malicious web page's fetch carries its own Origin, which is rejected.</summary>
    internal static bool IsOriginAllowed(string? origin, ISet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        return allowed.Contains(Norm(u.Host));
    }

    /// <summary>Always-on DNS-rebinding guard (runs even without a token): rejects requests
    /// whose Host or Origin header is not loopback / the bound host. The MCP spec calls for
    /// Origin validation on local HTTP servers; this closes the tokenless-loopback RCE that a
    /// visited web page could otherwise reach via DNS rebinding.</summary>
    internal static void UseWolvenKitOriginGuard(this WebApplication app, string url)
    {
        var allowed = AllowedHosts(url);
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host; // host only, no port
            var origin = context.Request.Headers.Origin.ToString();
            if (!IsHostAllowed(host, allowed) || !IsOriginAllowed(origin, allowed))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("403 Forbidden — host/origin not allowed (DNS-rebinding guard).");
                return;
            }
            await next();
        });
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
