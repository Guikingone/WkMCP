using WkMcp;
using Xunit;

namespace WkMcp.Tests;

/// <summary>Pure helpers of LiveTools (the live bridge itself requires the game).</summary>
public class LiveToolsTests
{
    [Theory]
    [InlineData("""{"id":"abc123"}""", "abc123")]
    [InlineData("""{"subscriptionId":"s-42"}""", "s-42")]
    [InlineData("""{"subscription_id":7}""", "7")]
    [InlineData("\"nu-id\"", "nu-id")]
    [InlineData("plain-id", "plain-id")]
    [InlineData("12345", "12345")]
    public void TryExtractSubscriptionId_recognizes_the_formats(string result, string expected)
        => Assert.Equal(expected, LiveTools.TryExtractSubscriptionId(result));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"autre":"champ"}""")]
    [InlineData("pas un id car espaces")]
    public void TryExtractSubscriptionId_rejects_the_noise(string? result)
        => Assert.Null(LiveTools.TryExtractSubscriptionId(result));
}
