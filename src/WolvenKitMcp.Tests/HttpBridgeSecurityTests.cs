using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

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
}
