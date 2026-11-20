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

    /// <summary>
    /// E29/F02/US01/T01 (NF-125): the regimes of the registration of the guests are off by default and follow <c>Features:GuestRegistrationRegimes</c> alone; the
    /// frontend reads them under <c>guestRegistrationRegimes</c>.
    /// </summary>
    [Fact]
    public void Get_GuestRegistrationRegimes_IsOffByDefault_AndFollowsItsConfigurationAlone()
    {
        Assert.False(Get(new Dictionary<string, string?>())["guestRegistrationRegimes"]);

        var on = Get(new Dictionary<string, string?> { [$"{FeatureFlags.SectionName}:{FeatureFlags.GuestRegistrationRegimes}"] = "true" });

        Assert.True(on["guestRegistrationRegimes"]);
        Assert.Single(on, feature => feature.Value);
    }
}
