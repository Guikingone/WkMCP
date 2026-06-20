using WkMcp;
using Xunit;

namespace WkMcp.Tests;

/// <summary>HTTP mode safeguards: loopback classification, fail-closed rule, token compare.</summary>
public class HttpBridgeSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:3001", true)]
    [InlineData("http://localhost:3001", true)]
    [InlineData("http://[::1]:3001", true)]
    [InlineData("http://127.0.0.5:80", true)]   // all of 127.0.0.0/8 is loopback
    [InlineData("127.0.0.1:3001", true)]        // without scheme
    [InlineData("http://0.0.0.0:3001", false)]  // all interfaces
    [InlineData("http://192.168.1.10:3001", false)]
    [InlineData("http://example.com:3001", false)]
    [InlineData("http://+:3001", false)]
    [InlineData("", false)]
    public void IsLoopback_classifies(string url, bool expected)
        => Assert.Equal(expected, HttpBridgeSecurity.IsLoopback(url));

    [Fact]
    public void CheckStartup_loopback_without_token_is_ok()
        => Assert.True(HttpBridgeSecurity.CheckStartup("http://127.0.0.1:3001", null).ok);

    [Fact]
    public void CheckStartup_nonloopback_without_token_is_refused()
    {
        var (ok, error) = HttpBridgeSecurity.CheckStartup("http://0.0.0.0:3001", null);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void CheckStartup_nonloopback_with_token_is_ok()
        => Assert.True(HttpBridgeSecurity.CheckStartup("http://0.0.0.0:3001", "s3cret").ok);

    [Theory]
    [InlineData("secret", "secret", true)]
    [InlineData("secret", "secreT", false)]   // case-sensitive
    [InlineData("secret", "", false)]
    [InlineData("", "secret", false)]
    [InlineData("a", "aa", false)]            // different lengths
    public void TokenEquals_compares(string provided, string expected, bool result)
        => Assert.Equal(result, HttpBridgeSecurity.TokenEquals(provided, expected));

    // ── DNS-rebinding guard (Host / Origin) ───────────────────────────────

    private static readonly ISet<string> Allowed =
        HttpBridgeSecurity.AllowedHosts("http://127.0.0.1:3001");

    [Fact]
    public void AllowedHosts_includes_loopback_aliases()
    {
        Assert.Contains("127.0.0.1", Allowed);
        Assert.Contains("localhost", Allowed);
        Assert.Contains("::1", Allowed);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("", true)]                       // header omitted by some local clients
    [InlineData("evil.attacker.com", false)]     // DNS-rebinding host
    [InlineData("169.254.169.254", false)]
    public void IsHostAllowed_filters(string host, bool expected)
        => Assert.Equal(expected, HttpBridgeSecurity.IsHostAllowed(host, Allowed));

    [Theory]
    [InlineData("", true)]                                 // non-browser client (no Origin)
    [InlineData("http://localhost", true)]
    [InlineData("http://127.0.0.1:3001", true)]
    [InlineData("http://evil.attacker.com", false)]        // cross-origin web page
    [InlineData("https://example.com", false)]
    [InlineData("not-a-url", false)]
    public void IsOriginAllowed_filters(string origin, bool expected)
        => Assert.Equal(expected, HttpBridgeSecurity.IsOriginAllowed(origin, Allowed));

    [Fact]
    public void AllowedHosts_includes_a_nonloopback_bound_host()
    {
        // When explicitly bound to a host (with token), that host is accepted too.
        var set = HttpBridgeSecurity.AllowedHosts("http://192.168.1.10:3001");
        Assert.Contains("192.168.1.10", set);
        Assert.Contains("127.0.0.1", set); // loopback aliases always present
    }
}
