using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

/// <summary>Helpers purs de LiveTools (le pont live lui-même exige le jeu).</summary>
public class LiveToolsTests
{
    [Theory]
    [InlineData("""{"id":"abc123"}""", "abc123")]
    [InlineData("""{"subscriptionId":"s-42"}""", "s-42")]
    [InlineData("""{"subscription_id":7}""", "7")]
    [InlineData("\"nu-id\"", "nu-id")]
    [InlineData("plain-id", "plain-id")]
    [InlineData("12345", "12345")]
    public void TryExtractSubscriptionId_reconnait_les_formats(string result, string expected)
        => Assert.Equal(expected, LiveTools.TryExtractSubscriptionId(result));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"autre":"champ"}""")]
    [InlineData("pas un id car espaces")]
    public void TryExtractSubscriptionId_rejette_le_bruit(string? result)
        => Assert.Null(LiveTools.TryExtractSubscriptionId(result));
}
