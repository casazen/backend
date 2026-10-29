using Casazen.Infrastructure.External;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// CF-STATUS-REAL: the status API key comes from a variable that a person pastes in Railway. A line break or a blank at its edge (a triple
/// click on a line of a password manager brings one) is not part of the key and must not reach the header, where it would make every read
/// fail. A key made only of blanks is no key: no header, and the status says "not available" instead of asking CF.
/// </summary>
public class ConcordatoFacileStatusKeyTests
{
    [Theory]
    [InlineData("cfsp_test_abc")]
    [InlineData("cfsp_test_abc\n")]
    [InlineData("  cfsp_test_abc\r\n")]
    public void StatusClient_KeyWithBlanksAtTheEdges_IsSentWithoutThem(string configured)
    {
        var http = StatusHttpClient(configured);

        var authorization = http.DefaultRequestHeaders.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization.Scheme);
        Assert.Equal("cfsp_test_abc", authorization.Parameter);
        Assert.Equal(new Uri("https://stato.concordatofacile.it"), http.BaseAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void StatusClient_NoKey_SendsNoAuthorization(string? configured)
    {
        Assert.Null(StatusHttpClient(configured).DefaultRequestHeaders.Authorization);
    }

    /// <summary>The typed client is a named client of the factory (named after its type): configured by the registration, nothing is sent.</summary>
    private static HttpClient StatusHttpClient(string? key)
    {
        var settings = new Dictionary<string, string?> { ["ConcordatoFacile:StatusApiKey"] = key };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddCasazenServices();

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ConcordatoFacileApiClient));
    }
}
