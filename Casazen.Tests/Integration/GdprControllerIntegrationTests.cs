using System.Net;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class GdprControllerIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public GdprControllerIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ExportOrgFiscal_AsStaffRole_Returns403()
    {
        var userId = $"auth0|gdpr-export-staff-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(userId);
        await SetFiscalIdentifiersAsync(org.Id, "RSSMRA80A01H501U", "12345678901");

        using var client = _factory.CreateAuthenticatedClient(userId, "Staff");
        var response = await client.GetAsync("/api/gdpr/org/export");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnonymizeOrgFiscal_AsStaffRole_Returns403_AndDoesNotMutateFiscalIdentifiers()
    {
        var userId = $"auth0|gdpr-staff-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(userId);
        await SetFiscalIdentifiersAsync(org.Id, "RSSMRA80A01H501U", "12345678901");

        using var client = _factory.CreateAuthenticatedClient(userId, "Staff");
        var response = await client.PostAsync("/api/gdpr/org/anonymize", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Orgs.FindAsync(org.Id);
        Assert.Equal("RSSMRA80A01H501U", persisted!.FiscalCode);
        Assert.Equal("12345678901", persisted.PartitaIvaNumber);
    }

    private async Task SetFiscalIdentifiersAsync(Guid orgId, string fiscalCode, string partitaIvaNumber)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        org!.FiscalCode = fiscalCode;
        org.PartitaIvaNumber = partitaIvaNumber;
        org.HasPartitaIva = true;
        await db.SaveChangesAsync();
    }
}
