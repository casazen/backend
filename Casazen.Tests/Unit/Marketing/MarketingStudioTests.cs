using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Casazen.Tests.Unit.Marketing;

public class MarketingStudioTests
{
    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://localhost/", false)]
    [InlineData("https://name:secret@example.org/", false)]
    [InlineData("https://villa.example.org/reserve?lang=it", true)]
    public void ExternalDestinationRequiresPublicHttpsHost(string value, bool expected) =>
        Assert.Equal(expected, MarketingStudio.IsPublicUrl(value));

    [Fact]
    public void TrackingPreservesDestinationAndIncludesStableCampaignIdentity()
    {
        var asset = new MarketingAsset { Destination = "https://villa.example.org/book?language=fr#availability", Channel = "newsletter" };
        var url = new Uri(MarketingStudio.TaggedDestination(asset));
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("fr", query["language"].ToString());
        Assert.Equal("newsletter", query["utm_medium"].ToString());
        Assert.Equal(asset.Id.ToString("D"), query["utm_campaign"].ToString());
        Assert.Equal("#availability", url.Fragment);
    }

    [Fact]
    public void ReadsFencedJsonAndRejectsPlainTextFromProvider()
    {
        var content = MarketingStudio.Parse<StudioCopy>("```json\n{\"subject\":\"Titolo\",\"body\":\"Un invito\"}\n```");
        Assert.Equal("Un invito", content.Body);
        Assert.Throws<DomainRuleException>(() => MarketingStudio.Parse<StudioCopy>("no provider response"));
    }
}
