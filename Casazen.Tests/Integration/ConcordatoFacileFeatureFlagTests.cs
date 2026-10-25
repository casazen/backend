using System.Net;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// CF-FIX2: the controller of the module takes its services in the constructor and one of them, the cache of the status, was never
    /// registered: with the flag on, no action of the controller (checkout and access included) could even be created. Every dependency
    /// of the constructor must be known to the container.
    /// </summary>
    [Fact]
    public void ConcordatoFacileController_EveryConstructorDependency_IsRegistered()
    {
        var isService = factory.Services.GetRequiredService<IServiceProviderIsService>();

        var missing = typeof(ConcordatoFacileController).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Where(type => !isService.IsService(type))
            .Select(type => type.Name)
            .ToList();

        Assert.Empty(missing);
    }
}

/// <summary>The default integration host with <c>Features:ConcordatoFacile=true</c>: a signed-in user reaches the service through the endpoints.</summary>
public sealed class ConcordatoFacileEnabledFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:ConcordatoFacile"] = "true" }));
    }
}

/// <summary>
/// The checkout takes its input from the route and the token, so the frontend may post it with no body and no Content-Type. The action
/// bound an empty <c>[FromBody]</c> class and the pipeline answered 415 before the action ran (the first payment on the test environment
/// failed this way). The other tests of the module send <c>{}</c> with a JSON Content-Type, which is why none of them saw it.
/// </summary>
public class ConcordatoFacileCheckoutRequestTests(ConcordatoFacileEnabledFactory factory) : IClassFixture<ConcordatoFacileEnabledFactory>
{
    [Fact]
    public async Task Checkout_PostWithoutBodyAndContentType_ReachesTheService_AndNeverAnswers415()
    {
        var userId = $"auth0|cf-checkout-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(userId);
        using var client = factory.CreateAuthenticatedClient(userId, "LongTermLandlord");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/leases/{Guid.NewGuid()}/concordatofacile/checkout");

        var response = await client.SendAsync(request);

        Assert.Null(request.Content);
        Assert.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        await LeaseDraftHttp.ExpectProblemAsync(response, HttpStatusCode.NotFound, "lease_not_found");
    }
}
