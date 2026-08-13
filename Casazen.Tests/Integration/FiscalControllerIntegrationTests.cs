using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Integration;

public class FiscalControllerIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public FiscalControllerIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetRegime_OneProperty_RecommendsCedolare21()
    {
        var owner = $"auth0|fiscal-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        await _factory.SeedPropertyAsync(owner);

        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/regime?taxYear=2026");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("strPropertyCount").GetInt32());
        Assert.False(body.GetProperty("requiresPartitaIva").GetBoolean());
        Assert.Equal("CedolareSecca21", body.GetProperty("properties")[0].GetProperty("recommendedRegime").GetString());
        Assert.Contains("informativa", body.GetProperty("disclaimer").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssignRegime_ForeignProperty_Returns404()
    {
        var ownerA = $"auth0|fiscal-a-{Guid.NewGuid():N}";
        var ownerB = $"auth0|fiscal-b-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerB);

        using var client = _factory.CreateAuthenticatedClient(ownerB, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/fiscal/properties/{property.Id}/regime", new
        {
            taxYear = 2026,
            regime = "CedolareSecca21",
            isPrimaryForCedolare = true,
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnnualReport_ContainsPackLabelNotTaxDue()
    {
        var owner = $"auth0|fiscal-r-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/reports/annual/2026");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("commercialista", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("irpef", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taxDue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnnualReportCsv_HasCsvContentType()
    {
        var owner = $"auth0|fiscal-csv-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/reports/annual/2026?format=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("FiscalCode", response.Content.Headers.ContentDisposition?.FileName ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
