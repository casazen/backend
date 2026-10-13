using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Infrastructure.Features;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// <c>GET /api/public/features</c> (FD-20): one camelCase key per flag of <see cref="FeatureFlags.All"/>. BL-01 adds
/// <c>uiRedesign</c>, off unless <c>Features:UiRedesign</c> is explicitly <c>true</c>.
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
    public void Get_DefaultConfiguration_ListsEveryFlagOff_UiRedesignIncluded()
    {
        var features = Get(new Dictionary<string, string?>());

        Assert.True(features.ContainsKey("uiRedesign"));
        Assert.False(features["uiRedesign"]);
        Assert.Equal(
            FeatureFlags.All.Select(JsonNamingPolicy.CamelCase.ConvertName).Order(),
            features.Keys.Order());
        Assert.All(features, feature => Assert.False(feature.Value, $"{feature.Key} must be off by default"));
    }

    [Fact]
    public void Get_UiRedesignOn_ReportsOnlyThatFlagOn()
    {
        var features = Get(new Dictionary<string, string?> { ["Features:UiRedesign"] = "true" });

        Assert.True(features["uiRedesign"]);
        Assert.All(features.Where(feature => feature.Key != "uiRedesign"), feature => Assert.False(feature.Value));
    }
}
