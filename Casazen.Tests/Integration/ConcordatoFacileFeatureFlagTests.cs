using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CF-FIX (product owner, 2026-10-25): the ConcordatoFacile service is behind <c>Features:ConcordatoFacile</c>, off by default and
/// in production until the trial run and the privacy framework are confirmed to ConcordatoFacile. Off, its endpoints answer 404 like
/// a route that does not exist, before authentication, so nothing can be paid and no draft made through them; on, they are reached
/// like any other endpoint (here: the authentication asks for the token).
/// </summary>
public class ConcordatoFacileFeatureFlagTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string CheckoutPath = "/api/leases/00000000-0000-0000-0000-000000000001/concordatofacile/checkout";
    private const string LookupPath = "/api/concordatofacile/practices/P-0123456789ABCDEF/lease";

    [Theory]
    [InlineData("POST", CheckoutPath)]
    [InlineData("GET", "/api/leases/00000000-0000-0000-0000-000000000001/concordatofacile/access")]
    [InlineData("GET", LookupPath)]
    [InlineData("POST", "/api/leases/drafts/concordatofacile")]
    public async Task ConcordatoFacileEndpoint_FlagOff_Returns404BeforeAuthentication(string method, string path)
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request(method, path));

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
    }

    [Fact]
    public async Task ConcordatoFacileEndpoint_FlagOn_IsReached_SoTheAuthenticationAsksForTheToken()
    {
        await using var enabled = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:ConcordatoFacile"] = "true" })));
        using var client = enabled.CreateClient();

        var checkout = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request("POST", CheckoutPath));
        var lookup = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request("GET", LookupPath));

        Assert.Equal(HttpStatusCode.Unauthorized, checkout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, lookup.StatusCode);
    }
}
