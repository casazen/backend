using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E40/F01/US01/T04 (NF-149, AC-NF149-3): the one rule of the Google Tag Manager container id. Only <c>GTM-</c> plus 4 to 12 letters or digits is
/// stored (normalized to upper case); an empty value clears the id; anything else is refused when saved, and a stored value that is no id is never
/// served to the pages.
/// </summary>
public class GtmContainerIdsTests
{
    [Theory]
    [InlineData("GTM-ABC123", "GTM-ABC123")]
    [InlineData(" gtm-abc123 ", "GTM-ABC123")]
    [InlineData("GTM-ABCD", "GTM-ABCD")]
    [InlineData("gtm-0123456789ab", "GTM-0123456789AB")]
    public void TryNormalize_ValidIds_AreUpperCased(string input, string expected)
    {
        Assert.True(GtmContainerIds.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_NullOrEmpty_ClearsTheId(string? input)
    {
        Assert.True(GtmContainerIds.TryNormalize(input, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("UA-1234-1")]
    [InlineData("G-ABCDEF1")]
    [InlineData("GTM-")]
    [InlineData("GTM-ABC")]
    [InlineData("GTM-AB CD")]
    [InlineData("GTM-<script>")]
    [InlineData("GTM-ABCDEFGHIJKLM")]
    [InlineData("https://www.googletagmanager.com/gtm.js?id=GTM-ABC123")]
    [InlineData("<script>GTM-ABC123</script>")]
    [InlineData("GTM-ABC12\n3")]
    [InlineData("GTM_ABC123")]
    [InlineData("GTM-ABCı23")]
    [InlineData("GTM-ABC-123")]
    public void TryNormalize_InvalidShapes_Rejected(string input)
    {
        Assert.False(GtmContainerIds.TryNormalize(input, out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void TryNormalize_ThirteenCharacterSuffix_Rejected()
    {
        Assert.Equal(13, "ABCDEFGHIJKLM".Length);
        Assert.False(GtmContainerIds.TryNormalize("GTM-ABCDEFGHIJKLM", out _));
        Assert.True(GtmContainerIds.TryNormalize("GTM-ABCDEFGHIJKL", out _));
    }

    [Theory]
    [InlineData("GTM-ABC123", true, "GTM-ABC123")]
    [InlineData("GTM-ABC123", false, null)]
    [InlineData(null, true, null)]
    [InlineData("not an id", true, null)]
    public void Served_OnlyAValidIdWithSiteEmbedOn(string? stored, bool siteEmbed, string? expected)
    {
        Assert.Equal(expected, GtmContainerIds.Served(stored, siteEmbed));
    }
}
