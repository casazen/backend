using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Infrastructure.Features;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PublicFeaturesControllerTests
{
    private static IReadOnlyDictionary<string, bool> Get(Dictionary<string, string?> configuration)
    {
        var flags = new ConfigurationFeatureFlags(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());

        var result = new PublicFeaturesController(flags).Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, bool>>(ok.Value);
    }

    [Fact]
    public void Get_DefaultConfiguration_ListsEveryFlagOff()
    {
        var features = Get(new Dictionary<string, string?>());

        Assert.Equal(
            FeatureFlags.All.Select(JsonNamingPolicy.CamelCase.ConvertName).Order(),
            features.Keys.Order());
        Assert.All(features, feature => Assert.False(feature.Value, $"{feature.Key} must be off by default"));
    }

    [Fact]
    public void Get_GuestRegistrationRegimes_IsOffByDefault_AndFollowsItsConfigurationAlone()
    {
        Assert.False(Get(new Dictionary<string, string?>())["guestRegistrationRegimes"]);

        var on = Get(new Dictionary<string, string?> { [$"{FeatureFlags.SectionName}:{FeatureFlags.GuestRegistrationRegimes}"] = "true" });

        Assert.True(on["guestRegistrationRegimes"]);
        Assert.Single(on, feature => feature.Value);
    }

    [Fact]
    public void Get_SiteIntegrationFlags_AreExposedUnderTheirCamelCaseKeys()
    {
        var off = Get(new Dictionary<string, string?>());
        var on = Get(new Dictionary<string, string?>
        {
            ["Features:WhiteLabelHost"] = "true",
            ["Features:Auth0HostSync"] = "true",
            ["Features:SiteEmbed"] = "true",
        });

        foreach (var key in new[] { "whiteLabelHost", "auth0HostSync", "siteEmbed" })
        {
            Assert.False(off[key]);
            Assert.True(on[key]);
        }
    }

    [Fact]
    public void Get_SelfHostedIdentityFlags_AreExposedOffByDefault_UnderTheirCamelCaseKeys()
    {
        var keys = new[] { "checkInIdentitySelfHosted", "checkInIdentityAutoApproval", "checkInIdentityReviewEvidence" };
        var off = Get(new Dictionary<string, string?>());
        var on = Get(new Dictionary<string, string?>
        {
            [$"{FeatureFlags.SectionName}:{FeatureFlags.CheckInIdentitySelfHosted}"] = "true",
            [$"{FeatureFlags.SectionName}:{FeatureFlags.CheckInIdentityAutoApproval}"] = "true",
            [$"{FeatureFlags.SectionName}:{FeatureFlags.CheckInIdentityReviewEvidence}"] = "true",
        });

        Assert.All(keys, key => Assert.False(off[key], $"{key} must be off by default"));
        Assert.All(keys, key => Assert.True(on[key]));
        Assert.False(on["checkInIdentityVerification"]);
        Assert.Equal(keys.Order(), on.Where(feature => feature.Value).Select(feature => feature.Key).Order());
    }
}
