using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
// OrgEntity is a global alias defined in Casazen.Tests.csproj: OrgEntity = global::Casazen.Core.Entities.Org

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-16 (A4-10, A9-32): the supplier iCal URL cannot point to an internal destination, and a sync error is shown as
/// a code with a localized message instead of the exception message.
/// </summary>
public class SupplierCalendarSyncIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public SupplierCalendarSyncIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/cal.ics")]
    [InlineData("https://10.0.0.1/cal.ics")]
    [InlineData("https://[::1]/cal.ics")]
    [InlineData("https://postgres.railway.internal/cal.ics")]
    public async Task SetIcalFeed_InternalOrNonHttpsUrl_Returns400WithCodeAndSavesNothing(string url)
    {
        var (supplierId, orgId) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/calendar/ical", new { icalFeedUrl = url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ICalErrorCodes.InvalidUrl, body.GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId);
        Assert.Null(profile.IcalFeedUrl);
        Assert.Equal(CalendarSyncType.None, profile.CalendarSyncType);
    }

    [Fact]
    public async Task GetCalendarStatus_WithLegacyExceptionMessage_ReturnsCodeWithoutTheMessage()
    {
        var (supplierId, _) = await SeedSupplierAsync(calendarSyncError: "Sync failed: Connection refused (169.254.169.254:80)");
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.GetAsync("/api/supplier/calendar/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("169.254.169.254", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(ICalErrorCodes.SyncFailed, body.GetProperty("calendarSyncErrorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("calendarSyncError").GetString()));
    }

    [Fact]
    public async Task GetCalendarStatus_WithStoredCode_ReturnsCodeAndLocalizedMessage()
    {
        var (supplierId, _) = await SeedSupplierAsync(calendarSyncError: ICalErrorCodes.Unreachable);
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var response = await client.GetAsync("/api/supplier/calendar/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ICalErrorCodes.Unreachable, body.GetProperty("calendarSyncErrorCode").GetString());
        Assert.StartsWith("The calendar could not be downloaded", body.GetProperty("calendarSyncError").GetString());
    }

    [Fact]
    public async Task GetDashboard_WithLegacyExceptionMessage_ReturnsCodeWithoutTheMessage()
    {
        var (supplierId, _) = await SeedSupplierAsync(calendarSyncError: "Sync failed: Name or service not known (db.internal:443)");
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.GetAsync("/api/supplier/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("db.internal", raw);
        var sync = JsonDocument.Parse(raw).RootElement.GetProperty("calendarSyncStatus");
        Assert.Equal(ICalErrorCodes.SyncFailed, sync.GetProperty("calendarSyncErrorCode").GetString());
    }

    private async Task<(string UserId, Guid OrgId)> SeedSupplierAsync(string? calendarSyncError = null)
    {
        var userId = $"auth0|supplier-{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new OrgEntity
        {
            Name = "Test Supplier Srl",
            Slug = $"test-supplier-{Guid.NewGuid():N}"[..30],
            DisplayName = "Test Supplier Srl",
            ContactEmail = $"{Guid.NewGuid():N}@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.Users.Add(new User
        {
            Id = userId,
            Email = org.ContactEmail,
            FirstName = "Test",
            LastName = "Supplier",
            OrgId = org.Id,
            IsActive = true,
        });
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = org.ContactEmail,
            LegalName = "Test Supplier Srl",
            Phone = "+39 06 999999",
            ComuniJson = "[\"H501\"]",
            CalendarSyncError = calendarSyncError,
            CalendarSyncType = calendarSyncError is null ? CalendarSyncType.None : CalendarSyncType.ICalFeed,
            IcalFeedUrl = calendarSyncError is null ? null : "https://feeds.example.com/cal.ics",
        });

        await db.SaveChangesAsync();
        return (userId, org.Id);
    }
}
