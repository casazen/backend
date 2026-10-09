using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Services;
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
    public async Task ExportOrgFiscal_AsPropertyScopedOwner_Returns403()
    {
        var ownerId = $"auth0|gdpr-export-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var scopedOwnerId = $"auth0|gdpr-export-scoped-{Guid.NewGuid():N}";
        await SeedUserInOrgAsync(scopedOwnerId, org.Id);
        await SetFiscalIdentifiersAsync(org.Id, "RSSMRA80A01H501U", "12345678901");

        using var client = _factory.CreateAuthenticatedClient(scopedOwnerId, "PropertyOwner");
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

    [Fact]
    public async Task AnonymizeOrgFiscal_AsPropertyScopedOwner_Returns403_AndDoesNotMutateOrgFiscalData()
    {
        var ownerId = $"auth0|gdpr-org-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var scopedOwnerId = $"auth0|gdpr-scoped-owner-{Guid.NewGuid():N}";
        await SeedUserInOrgAsync(scopedOwnerId, org.Id);
        await SetFiscalIdentifiersAsync(org.Id, "RSSMRA80A01H501U", "12345678901");
        var propertyId = await SeedPropertyTaxpayerAsync(org.Id, scopedOwnerId, "VRDLGI80A01H501Y");

        using var client = _factory.CreateAuthenticatedClient(scopedOwnerId, "PropertyOwner");
        var response = await client.PostAsync("/api/gdpr/org/anonymize", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Orgs.FindAsync(org.Id);
        var property = await db.Properties.FindAsync(propertyId);
        Assert.Equal("RSSMRA80A01H501U", persisted!.FiscalCode);
        Assert.Equal("12345678901", persisted.PartitaIvaNumber);
        Assert.Equal("VRDLGI80A01H501Y", property!.TaxpayerFiscalCode);
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

    private async Task SeedUserInOrgAsync(string userId, Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Scoped",
            LastName = "Owner",
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedPropertyTaxpayerAsync(Guid orgId, string ownerId, string taxpayerFiscalCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = orgId,
            Name = "GDPR fiscal property",
            Address = $"Via GDPR {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            TaxpayerFiscalCode = taxpayerFiscalCode,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }
}
