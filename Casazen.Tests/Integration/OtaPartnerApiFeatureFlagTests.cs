using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Infrastructure.OTA;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-20 / D10 (A2-09, A9-17, R-10): the OTA partner API is in freeze behind <c>Features:OtaPartnerApi</c>, off by
/// default. Every OTA endpoint answers 404 like a missing route, before authentication; the flag is exposed to the
/// frontend by <c>GET /api/public/features</c>.
/// </summary>
public class OtaPartnerApiFeatureFlagTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Theory]
    [InlineData("GET", "/api/ota?propertyId={propertyId}")]
    [InlineData("POST", "/api/ota/sync?propertyId={propertyId}")]
    [InlineData("GET", "/api/ota/status?propertyId={propertyId}")]
    [InlineData("GET", "/api/properties/{propertyId}/ota-integrations")]
    [InlineData("POST", "/api/properties/{propertyId}/ota-integrations")]
    [InlineData("DELETE", "/api/properties/{propertyId}/ota-integrations/00000000-0000-0000-0000-000000000001")]
    public async Task OtaEndpoint_FlagOffAsPropertyOwner_Returns404(string method, string path)
    {
        var property = await factory.SeedPropertyAsync();
        using var client = factory.CreateAuthenticatedClient();
        factory.BackgroundJobClientMock.Invocations.Clear();

        var response = await client.SendAsync(Request(method, path.Replace("{propertyId}", property.Id.ToString())));

        await AssertNotFoundProblemAsync(response);
        factory.BackgroundJobClientMock.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task OtaEndpoint_FlagOffAnonymous_Returns404NotUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/ota?propertyId={Guid.NewGuid()}");

        await AssertNotFoundProblemAsync(response);
    }

    [Fact]
    public async Task OtaWebhook_FlagOffWithoutWebhookSecret_Returns404InsteadOf500()
    {
        using var client = factory.CreateClient();
        factory.BackgroundJobClientMock.Invocations.Clear();

        var response = await client.SendAsync(Request("POST", $"/webhooks/ota/airbnb?propertyId={Guid.NewGuid()}"));

        await AssertNotFoundProblemAsync(response);
        factory.BackgroundJobClientMock.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task PublicFeatures_Default_ReturnsOtaPartnerApiOff()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("otaPartnerApi").GetBoolean());
    }

    [Fact]
    public void OtaAdapters_FlagOff_AreNotInTheContainer()
    {
        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<AirbnbAdapter>());
        Assert.Null(scope.ServiceProvider.GetService<BookingComAdapter>());
        var channelFactory = scope.ServiceProvider.GetRequiredService<IChannelFactory>();
        Assert.ThrowsAny<InvalidOperationException>(() => channelFactory.GetAdapter("airbnb"));
    }

    internal static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    /// <summary>Same response as a route that does not exist: 404 problem with code <c>not_found</c>.</summary>
    internal static async Task AssertNotFoundProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", body.RootElement.GetProperty("code").GetString());
    }
}

/// <summary>
/// FD-20 with <c>Features:OtaPartnerApi</c> on: the frozen endpoints come back, without the removed ones
/// (<c>PUT pricing</c> rewrote <c>NightlyRate</c> unvalidated, <c>validate?apiKey=</c> put the key in the URL,
/// <c>sync-platform</c> had no ownership check: A9-17).
/// </summary>
public class OtaPartnerApiFlagOnTests(OtaPartnerApiEnabledFactory factory) : IClassFixture<OtaPartnerApiEnabledFactory>
{
    [Fact]
    public async Task GetIntegrations_FlagOnAsOwner_Returns200()
    {
        var property = await factory.SeedPropertyAsync();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/ota?propertyId={property.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SyncAll_FlagOnAsOwner_QueuesSyncForTheProperty()
    {
        var property = await factory.SeedPropertyAsync();
        using var client = factory.CreateAuthenticatedClient();
        factory.BackgroundJobClientMock.Invocations.Clear();

        var response = await client.PostAsync($"/api/ota/sync?propertyId={property.Id}", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        factory.BackgroundJobClientMock.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public async Task SyncAll_FlagOnAsOtherTenant_Returns404AndQueuesNothing()
    {
        var property = await factory.SeedPropertyAsync();
        await factory.SeedOrgForOwnerAsync("auth0|fd20-other-owner");
        using var client = factory.CreateAuthenticatedClient(userId: "auth0|fd20-other-owner");
        factory.BackgroundJobClientMock.Invocations.Clear();

        var response = await client.PostAsync($"/api/ota/sync?propertyId={property.Id}", null);

        // The property of another tenant is not visible (tenant filter): no sync can be queued for it (A9-17).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        factory.BackgroundJobClientMock.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Theory]
    [InlineData("PUT", "/api/ota/pricing?propertyId={propertyId}&newPrice=-1")]
    [InlineData("POST", "/api/ota/validate?platform=airbnb&apiKey=sk_live_in_query")]
    [InlineData("POST", "/api/ota/sync-platform?platform=airbnb&externalId=someone-elses-listing")]
    public async Task RemovedOtaEndpoint_FlagOn_Returns404(string method, string path)
    {
        var property = await factory.SeedPropertyAsync(nightlyRate: 120m);
        using var client = factory.CreateAuthenticatedClient();
        factory.BackgroundJobClientMock.Invocations.Clear();

        var response = await client.SendAsync(
            OtaPartnerApiFeatureFlagTests.Request(method, path.Replace("{propertyId}", property.Id.ToString())));

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
        factory.BackgroundJobClientMock.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task PublicFeatures_FlagOn_ReturnsOtaPartnerApiOn()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("otaPartnerApi").GetBoolean());
    }
}
