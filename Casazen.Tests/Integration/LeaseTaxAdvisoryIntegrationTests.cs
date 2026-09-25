using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-08 over the real pipeline on PostgreSQL: the lease tax advisory with the committed configuration (validated at
/// startup), the data CasaZen does not hold sent in the body of the POST, input validation and tenant isolation.
/// </summary>
public class LeaseTaxAdvisoryIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string LongTermLandlord = "LongTermLandlord";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A day of 2026, the tax year of the committed IRPEF brackets: the result does not depend on the real clock.</summary>
    private static readonly DateTimeOffset In2026 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    private readonly CasazenWebApplicationFactory _factory;

    public LeaseTaxAdvisoryIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task Advisory_WithoutAndWithLandlordData_ComputesOnlyWhatItCan()
    {
        var landlord = UniqueUser("owner");
        var property = await _factory.SeedPropertyAsync(landlord);
        await using var app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(In2026));
        }));
        using var client = CreateClient(app, landlord, LongTermLandlord);
        var leaseId = await CreateLeaseAsync(client, property.Id);

        var plain = await ReadJsonAsync(await client.GetAsync($"/api/leases/{leaseId}/rli/advisory"));
        Assert.Equal(0.21m, plain.GetProperty("cedolare").GetProperty("rate").GetDecimal());
        Assert.Equal(2268.00m, plain.GetProperty("cedolare").GetProperty("annualTaxEur").GetDecimal());
        Assert.Equal(0m, plain.GetProperty("cedolare").GetProperty("registroEur").GetDecimal());
        var ordinary = plain.GetProperty("ordinary");
        Assert.Equal(216.00m, ordinary.GetProperty("registro").GetProperty("firstYearEur").GetDecimal());
        Assert.Equal("InputRequired", ordinary.GetProperty("bollo").GetProperty("status").GetString());
        Assert.Equal("InputRequired", ordinary.GetProperty("irpef").GetProperty("status").GetString());

        var response = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/rli/advisory",
            new { writtenPages = 6, copies = 2, otherTaxableIncomeEur = 20_000m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var computed = (await ReadJsonAsync(response)).GetProperty("ordinary");
        Assert.Equal("Computed", computed.GetProperty("bollo").GetProperty("status").GetString());
        Assert.Equal(64.00m, computed.GetProperty("bollo").GetProperty("amountEur").GetDecimal());
        Assert.Equal("Computed", computed.GetProperty("irpef").GetProperty("status").GetString());
        // 900 €/month: 10.260 € after the 5% reduction; 20.000 + 10.260 crosses 28.000 € (23% then 33%).
        Assert.Equal(2585.80m, computed.GetProperty("irpef").GetProperty("additionalGrossIrpefEur").GetDecimal());
    }

    [PostgresFact]
    public async Task Advisory_WithInvalidData_Returns400()
    {
        var landlord = UniqueUser("invalid");
        var property = await _factory.SeedPropertyAsync(landlord);
        using var client = _factory.CreateAuthenticatedClient(landlord, LongTermLandlord);
        var leaseId = await CreateLeaseAsync(client, property.Id);

        foreach (var body in new object[]
                 {
                     new { writtenPages = 0, copies = 1 },
                     new { writtenPages = 4, copies = 0 },
                     new { otherTaxableIncomeEur = -1m },
                 })
        {
            var response = await client.PostAsJsonAsync($"/api/leases/{leaseId}/rli/advisory", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Advisory_OfAnotherOrgsLease_IsNotReturned()
    {
        var owner = UniqueUser("owner2");
        var outsider = UniqueUser("outsider");
        var property = await _factory.SeedPropertyAsync(owner);
        await _factory.SeedOrgForOwnerAsync(outsider);
        using var ownerClient = _factory.CreateAuthenticatedClient(owner, LongTermLandlord);
        var leaseId = await CreateLeaseAsync(ownerClient, property.Id);
        using var client = _factory.CreateAuthenticatedClient(outsider, $"{LongTermLandlord},PropertyManager");

        var response = await client.PostAsJsonAsync($"/api/leases/{leaseId}/rli/advisory", new { writtenPages = 4, copies = 1 });

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"answered {(int)response.StatusCode}");
    }

    private static async Task<Guid> CreateLeaseAsync(HttpClient client, Guid propertyId)
    {
        var create = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId,
            contractType = "Libero",
            taxRegime = "CedolareSecca",
            startDate = "2026-11-01T00:00:00Z",
            endDate = "2030-10-31T00:00:00Z",
            monthlyRent = 900m,
            parties = new object[]
            {
                new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501Z", citizenship = "IT", contactEmail = "mario@example.com" },
                new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205X", citizenship = "IT", contactEmail = "giulia@example.com" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await ReadJsonAsync(create)).GetProperty("id").GetGuid();
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> app, string userId, string roles)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return client;
    }

    private static string UniqueUser(string role) => $"auth0|lt08-{role}-{Guid.NewGuid():N}";

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"answered {(int)response.StatusCode}");
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }
}
