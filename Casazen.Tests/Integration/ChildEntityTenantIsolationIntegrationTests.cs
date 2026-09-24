using System.Net;
using System.Net.Http.Json;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// TN-2 on the FD-04 PostgreSQL factory: child rows of org A (property documents, OTA integrations,
/// pricing config and history, Alloggiati reports, check-in sessions) are neither visible nor writable by
/// a user of org B, over HTTP and directly through the tenant query filter of <see cref="AppDbContext"/>
/// (also with B's own parent in the URL), and the rows created by org A carry org A's OrgId.
/// Runs with <c>Features:OtaPartnerApi</c> on (FD-20): otherwise the OTA endpoints answer 404 for everyone and the
/// OTA cases would no longer test the tenant boundary.
/// </summary>
public class ChildEntityTenantIsolationIntegrationTests : IClassFixture<OtaPartnerApiEnabledFactory>
{
    private const string OwnerRole = "PropertyOwner";

    private readonly CasazenWebApplicationFactory _factory;

    public ChildEntityTenantIsolationIntegrationTests(OtaPartnerApiEnabledFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task PropertyDocuments_UserOfOtherOrg_CannotListOrDeleteThem()
    {
        var s = await SeedTwoOrgsAsync();
        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);

        var list = await clientB.GetAsync($"/api/properties/{s.PropertyA.Id}/documents");
        var deleteWithOwnProperty = await clientB.DeleteAsync($"/api/properties/{s.PropertyB.Id}/documents/{s.DocumentA}");
        var deleteWithOtherProperty = await clientB.DeleteAsync($"/api/properties/{s.PropertyA.Id}/documents/{s.DocumentA}");

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteWithOwnProperty.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteWithOtherProperty.StatusCode);
        using var scope = _factory.Services.CreateScope();
        await using var db = NewDb(scope);
        Assert.True(await db.PropertyDocuments.AnyAsync(d => d.Id == s.DocumentA));
    }

    [PostgresFact]
    public async Task OtaIntegrations_UserOfOtherOrg_CannotReadUpdateOrDeleteThem()
    {
        var s = await SeedTwoOrgsAsync();
        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);

        var get = await clientB.GetAsync($"/api/properties/{s.PropertyB.Id}/ota-integrations/{s.IntegrationA}");
        var list = await clientB.GetAsync($"/api/properties/{s.PropertyA.Id}/ota-integrations");
        var update = await clientB.PutAsJsonAsync(
            $"/api/properties/{s.PropertyB.Id}/ota-integrations/{s.IntegrationA}",
            new { externalPropertyId = "hijacked", isActive = false });
        var delete = await clientB.DeleteAsync($"/api/properties/{s.PropertyB.Id}/ota-integrations/{s.IntegrationA}");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        using var scope = _factory.Services.CreateScope();
        await using var db = NewDb(scope);
        var integration = await db.OtaIntegrations.AsNoTracking().SingleAsync(o => o.Id == s.IntegrationA);
        Assert.Equal("ext-a", integration.ExternalPropertyId);
        Assert.True(integration.IsActive);
    }

    [PostgresFact]
    public async Task PricingConfigAndHistory_UserOfOtherOrg_CannotReadOrOverwriteThem()
    {
        var s = await SeedTwoOrgsAsync();
        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);

        var config = await clientB.GetAsync($"/api/pricing-adapter/config/{s.PropertyA.Id}");
        var history = await clientB.GetAsync($"/api/pricing-adapter/history/{s.PropertyA.Id}");
        var overwrite = await clientB.PostAsJsonAsync(
            $"/api/pricing-adapter/config/{s.PropertyA.Id}",
            new { isEnabled = false, adaptationFrequency = "weekly", includeSeasonality = false, includePublicHolidays = false });

        Assert.Equal(HttpStatusCode.NotFound, config.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, overwrite.StatusCode);
        using var scope = _factory.Services.CreateScope();
        await using var db = NewDb(scope);
        var configA = await db.PricingAdapterConfigs.AsNoTracking().SingleAsync(c => c.PropertyId == s.PropertyA.Id);
        Assert.True(configA.IsEnabled);
        Assert.Equal("daily", configA.AdaptationFrequency);
        Assert.Equal(2, await db.PricingHistories.CountAsync(h => h.PropertyId == s.PropertyA.Id));
    }

    [PostgresFact]
    public async Task ChildRows_TenantFilterOfOtherOrg_HidesThemFromReadsAndBulkWrites()
    {
        var s = await SeedTwoOrgsAsync();
        using var scope = _factory.Services.CreateScope();

        await using (var dbB = NewDb(scope, new FixedTenantContext(s.PropertyB.OrgId)))
        {
            Assert.Null(await dbB.PropertyDocuments.FindAsync(s.DocumentA));
            Assert.Null(await dbB.OtaIntegrations.FindAsync(s.IntegrationA));
            Assert.Null(await dbB.PricingAdapterConfigs.FirstOrDefaultAsync(c => c.PropertyId == s.PropertyA.Id));
            Assert.Empty(await dbB.PricingHistories.Where(h => h.PropertyId == s.PropertyA.Id).ToListAsync());
            Assert.Null(await dbB.AlloggiatiWebReports.FindAsync(s.ReportA));
            Assert.Null(await dbB.GuestCheckInSessions.FindAsync(s.SessionA));

            Assert.Equal(0, await dbB.PropertyDocuments.Where(d => d.Id == s.DocumentA).ExecuteDeleteAsync());
            Assert.Equal(0, await dbB.PricingHistories.Where(h => h.PropertyId == s.PropertyA.Id).ExecuteDeleteAsync());
            Assert.Equal(0, await dbB.AlloggiatiWebReports
                .Where(r => r.Id == s.ReportA)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, AlloggiatiWebStatus.Failed)));
            Assert.Equal(0, await dbB.GuestCheckInSessions
                .Where(x => x.Id == s.SessionA)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, GuestCheckInSessionStatus.Scaduto)));

            // B keeps seeing its own child rows.
            Assert.NotNull(await dbB.PropertyDocuments.FindAsync(s.DocumentB));
        }

        await using var dbA = NewDb(scope, new FixedTenantContext(s.PropertyA.OrgId));
        Assert.NotNull(await dbA.PropertyDocuments.FindAsync(s.DocumentA));
        Assert.Equal(2, await dbA.PricingHistories.CountAsync(h => h.PropertyId == s.PropertyA.Id));
        Assert.Equal(AlloggiatiWebStatus.Pending, (await dbA.AlloggiatiWebReports.FindAsync(s.ReportA))!.Status);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, (await dbA.GuestCheckInSessions.FindAsync(s.SessionA))!.Status);
        Assert.Null(await dbA.PropertyDocuments.FindAsync(s.DocumentB));
    }

    [PostgresFact]
    public async Task CreateChildRows_OwnerOfProperty_StampsThePropertyOrgId()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);

        var integration = await client.PostAsJsonAsync($"/api/properties/{property.Id}/ota-integrations", new
        {
            propertyId = property.Id,
            platform = "Airbnb",
            externalPropertyId = "ext-new",
            apiKey = "key-12345678",
        });
        var config = await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", new
        {
            isEnabled = true,
            adaptationFrequency = "daily",
            includeSeasonality = true,
            includePublicHolidays = false,
        });

        Assert.Equal(HttpStatusCode.Created, integration.StatusCode);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        using var scope = _factory.Services.CreateScope();
        await using var db = NewDb(scope);
        Assert.Equal(property.OrgId, await db.OtaIntegrations.Where(o => o.PropertyId == property.Id).Select(o => o.OrgId).SingleAsync());
        Assert.Equal(property.OrgId, await db.PricingAdapterConfigs.Where(c => c.PropertyId == property.Id).Select(c => c.OrgId).SingleAsync());
    }

    private sealed record TwoOrgs(
        string OwnerA,
        string OwnerB,
        Property PropertyA,
        Property PropertyB,
        Guid DocumentA,
        Guid DocumentB,
        Guid IntegrationA,
        Guid ReportA,
        Guid SessionA);

    private async Task<TwoOrgs> SeedTwoOrgsAsync()
    {
        var ownerA = NewOwner();
        var ownerB = NewOwner();
        var propertyA = await _factory.SeedPropertyAsync(ownerA);
        var propertyB = await _factory.SeedPropertyAsync(ownerB);
        Assert.NotEqual(propertyA.OrgId, propertyB.OrgId);

        using var scope = _factory.Services.CreateScope();
        await using var db = NewDb(scope);
        var now = DateTime.UtcNow;
        var documentA = NewDocument(propertyA, "cin-a.pdf");
        var documentB = NewDocument(propertyB, "cin-b.pdf");
        var integrationA = new OtaIntegration
        {
            PropertyId = propertyA.Id,
            OrgId = propertyA.OrgId,
            Platform = "Airbnb",
            ExternalPropertyId = "ext-a",
            ApiKey = "secret-key-a",
            IsActive = true,
        };
        db.PropertyDocuments.AddRange(documentA, documentB);
        db.OtaIntegrations.Add(integrationA);
        db.PricingAdapterConfigs.Add(new PricingAdapterConfig
        {
            PropertyId = propertyA.Id,
            OrgId = propertyA.OrgId,
            IsEnabled = true,
            AdaptationFrequency = "daily",
        });
        for (var i = 0; i < 2; i++)
        {
            db.PricingHistories.Add(new PricingHistory
            {
                PropertyId = propertyA.Id,
                OrgId = propertyA.OrgId,
                AdaptationDate = now.AddDays(-i),
                PreviousPrice = 100m,
                NewPrice = 110m,
                ChangeReason = "season",
                SyncStatus = "Pending",
            });
        }

        var guestA = new Guest { OrgId = propertyA.OrgId, FirstName = "Mario", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Guests.Add(guestA);
        var bookingA = new Booking
        {
            PropertyId = propertyA.Id,
            OrgId = propertyA.OrgId,
            GuestId = guestA.Id,
            CheckInDate = now.Date.AddDays(3),
            CheckOutDate = now.Date.AddDays(5),
            NumberOfGuests = 1,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        db.Bookings.Add(bookingA);
        var reportA = new AlloggiatiWebReport { BookingId = bookingA.Id, GuestId = guestA.Id, OrgId = propertyA.OrgId };
        var sessionA = new GuestCheckInSession
        {
            BookingId = bookingA.Id,
            OrgId = propertyA.OrgId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = now.AddDays(7),
        };
        db.AlloggiatiWebReports.Add(reportA);
        db.GuestCheckInSessions.Add(sessionA);
        await db.SaveChangesAsync();

        return new TwoOrgs(ownerA, ownerB, propertyA, propertyB, documentA.Id, documentB.Id, integrationA.Id, reportA.Id, sessionA.Id);
    }

    private static PropertyDocument NewDocument(Property property, string fileName) => new()
    {
        PropertyId = property.Id,
        OrgId = property.OrgId,
        FileName = fileName,
        StorageUrl = $"documents/{property.Id:N}/{fileName}",
        DocumentType = DocumentType.CinCertificate,
        UploadedBy = property.OwnerId,
    };

    // Without a tenant context the filter is off, like in a background job (no HTTP request in the scope).
    private static AppDbContext NewDb(IServiceScope scope, ITenantContext? tenant = null) =>
        new(
            scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(),
            tenant,
            scope.ServiceProvider.GetService<IDataProtectionProvider>());

    private static string NewOwner() => $"auth0|tn2-{Guid.NewGuid():N}";

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
