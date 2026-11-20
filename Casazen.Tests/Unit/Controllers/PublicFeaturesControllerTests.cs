using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Infrastructure.Features;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// <c>GET /api/public/features</c> (FD-20): one camelCase key per flag of <see cref="FeatureFlags.All"/>.
/// </summary>
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
    public void Get_SiteIntegrationFlags_AreExposedUnderTheirCamelCaseKeys()
    {
        // E40/F01/US01/T01: the frontend reads whiteLabelHost, auth0HostSync and siteEmbed (off by default, on only when set).
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
}
