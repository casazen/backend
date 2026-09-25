using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-11 (A4-15, decision D12) over the real pipeline on PostgreSQL: the supplier dashboard KPIs count the
/// supplier org's service requests (the supplier jobs, which no real flow created, are gone), by Europe/Rome calendar
/// dates, and only for the caller's supplier org. The supplier job and QR check-in routes answer 404.
/// </summary>
public class SupplierKpisPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Host = "PropertyOwner";
    private const string Supplier = "Supplier";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>15 September 2026, 10:00 in Rome.</summary>
    private static readonly DateTimeOffset MidSeptember = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    private readonly CasazenWebApplicationFactory _factory;

    public SupplierKpisPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task GetKpis_FiveRequestsCompletedThroughTheApi_ReportsFiveCompleted()
    {
        var w = await SeedWorldAsync();
        using (var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host))
        using (var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier))
        {
            for (var i = 0; i < 5; i++)
            {
                var id = await CreateRequestAsync(host, w, w.SupplierA.OrgId);
                await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { }));
                await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { }));
            }
        }

        // Clock fixed once the five completions are done: the last 30 days hold them whatever the real date.
        await using var app = WithClock(new FixedTimeProvider(DateTimeOffset.UtcNow));
        using var client = CreateClient(app, w.SupplierA.UserId, Supplier);

        var kpis = await GetKpisAsync(client, "Last30Days");

        Assert.Equal(5, kpis.GetProperty("completed").GetInt32());
        Assert.Equal(0, kpis.GetProperty("upcoming").GetInt32());
        Assert.Equal(0, kpis.GetProperty("awaitingAcceptance").GetInt32());
        Assert.Equal(0, kpis.GetProperty("rejected").GetInt32());
        Assert.Equal(5, kpis.GetProperty("totalRequests").GetInt32());
    }

    [PostgresFact]
    public async Task GetKpis_RequestsInEveryState_CountsCompletedAndRejectedInThePeriodAndOpenWorkNow()
    {
        var w = await SeedWorldAsync();
        var supplierOrg = w.SupplierA.OrgId;
        await AddRequestsAsync(
            w,
            Request(supplierOrg, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 2, 9)),
            Request(supplierOrg, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 5, 9)),
            Request(supplierOrg, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 8, 9)),
            Request(supplierOrg, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 10, 9)),
            // Paid after its completion: still a completed request.
            Request(supplierOrg, ServiceRequestStatus.Pagato, completedAt: Utc(2026, 9, 12, 9)),
            // Completed in August: not this month.
            Request(supplierOrg, ServiceRequestStatus.Completato, completedAt: Utc(2026, 8, 20, 9)),
            Request(supplierOrg, ServiceRequestStatus.Richiesto),
            Request(supplierOrg, ServiceRequestStatus.PresoInCarico),
            Request(supplierOrg, ServiceRequestStatus.InCorso),
            Request(supplierOrg, ServiceRequestStatus.Rifiutato, updatedAt: Utc(2026, 9, 9, 9)),
            Request(supplierOrg, ServiceRequestStatus.Rifiutato, updatedAt: Utc(2026, 8, 10, 9)));
        await using var app = WithClock(new FixedTimeProvider(MidSeptember));
        using var client = CreateClient(app, w.SupplierA.UserId, Supplier);

        var month = await GetKpisAsync(client, "CurrentMonth");
        var previousMonth = await GetKpisAsync(client, "PreviousMonth");
        var byDefault = await GetKpisAsync(client, period: null);

        Assert.Equal("CurrentMonth", month.GetProperty("period").GetString());
        Assert.Equal("2026-09-01", month.GetProperty("from").GetString());
        Assert.Equal("2026-09-15", month.GetProperty("to").GetString());
        Assert.Equal(RomeCalendar.TimeZoneId, month.GetProperty("timeZone").GetString());
        Assert.Equal(5, month.GetProperty("completed").GetInt32());
        Assert.Equal(1, month.GetProperty("rejected").GetInt32());
        Assert.Equal(1, month.GetProperty("awaitingAcceptance").GetInt32());
        Assert.Equal(2, month.GetProperty("upcoming").GetInt32());
        Assert.Equal(11, month.GetProperty("totalRequests").GetInt32());

        Assert.Equal("2026-08-01", previousMonth.GetProperty("from").GetString());
        Assert.Equal("2026-08-31", previousMonth.GetProperty("to").GetString());
        Assert.Equal(1, previousMonth.GetProperty("completed").GetInt32());
        Assert.Equal(1, previousMonth.GetProperty("rejected").GetInt32());
        // The open work is "now", whatever the period.
        Assert.Equal(1, previousMonth.GetProperty("awaitingAcceptance").GetInt32());
        Assert.Equal(2, previousMonth.GetProperty("upcoming").GetInt32());

        Assert.Equal(month.GetRawText(), byDefault.GetRawText());
    }

    [PostgresFact]
    public async Task GetKpis_CompletedAroundRomeMidnight_CountsByTheRomeCalendarDate()
    {
        var w = await SeedWorldAsync();
        var supplierOrg = w.SupplierA.OrgId;
        await AddRequestsAsync(
            w,
            // 31 August 23:30 in Rome (CEST, UTC+2): August.
            Request(supplierOrg, ServiceRequestStatus.Completato, completedAt: new DateTime(2026, 8, 31, 21, 30, 0, DateTimeKind.Utc)),
            // 1 September 00:30 in Rome, still 31 August in UTC: September.
            Request(supplierOrg, ServiceRequestStatus.Completato, completedAt: new DateTime(2026, 8, 31, 22, 30, 0, DateTimeKind.Utc)),
            // 15 September 23:30 in Rome: today, the last day of the period.
            Request(supplierOrg, ServiceRequestStatus.Completato, completedAt: new DateTime(2026, 9, 15, 21, 30, 0, DateTimeKind.Utc)));
        await using var app = WithClock(new FixedTimeProvider(MidSeptember));
        using var client = CreateClient(app, w.SupplierA.UserId, Supplier);

        var september = await GetKpisAsync(client, "CurrentMonth");
        var august = await GetKpisAsync(client, "PreviousMonth");

        Assert.Equal(2, september.GetProperty("completed").GetInt32());
        Assert.Equal(1, august.GetProperty("completed").GetInt32());
    }

    [PostgresFact]
    public async Task GetKpis_AnotherSupplierOrgRequests_AreNeverCounted()
    {
        var w = await SeedWorldAsync();
        await AddRequestsAsync(
            w,
            Request(w.SupplierA.OrgId, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 3, 9)),
            Request(w.SupplierA.OrgId, ServiceRequestStatus.Richiesto),
            Request(w.SupplierB.OrgId, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 4, 9)),
            Request(w.SupplierB.OrgId, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 5, 9)),
            Request(w.SupplierB.OrgId, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 6, 9)),
            Request(w.SupplierB.OrgId, ServiceRequestStatus.PresoInCarico),
            Request(w.SupplierB.OrgId, ServiceRequestStatus.Rifiutato, updatedAt: Utc(2026, 9, 7, 9)));
        await using var app = WithClock(new FixedTimeProvider(MidSeptember));
        using var supplierA = CreateClient(app, w.SupplierA.UserId, Supplier);
        using var supplierB = CreateClient(app, w.SupplierB.UserId, Supplier);

        var a = await GetKpisAsync(supplierA, "CurrentMonth");
        var b = await GetKpisAsync(supplierB, "CurrentMonth");

        Assert.Equal(1, a.GetProperty("completed").GetInt32());
        Assert.Equal(1, a.GetProperty("awaitingAcceptance").GetInt32());
        Assert.Equal(0, a.GetProperty("upcoming").GetInt32());
        Assert.Equal(0, a.GetProperty("rejected").GetInt32());
        Assert.Equal(2, a.GetProperty("totalRequests").GetInt32());

        Assert.Equal(3, b.GetProperty("completed").GetInt32());
        Assert.Equal(0, b.GetProperty("awaitingAcceptance").GetInt32());
        Assert.Equal(1, b.GetProperty("upcoming").GetInt32());
        Assert.Equal(1, b.GetProperty("rejected").GetInt32());
        Assert.Equal(5, b.GetProperty("totalRequests").GetInt32());
    }

    [PostgresTheory]
    [InlineData("9")]
    [InlineData("LastCentury")]
    public async Task GetKpis_UnknownPeriod_Returns400ValidationError(string period)
    {
        var w = await SeedWorldAsync();
        using var client = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);

        var response = await client.GetAsync($"/api/supplier/dashboard/kpis?period={period}");

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {text}");
        Assert.Equal("validation_error", JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task GetDashboard_ActiveSupplier_HasNoSupplierJobCounters()
    {
        var w = await SeedWorldAsync();
        using var client = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);

        var response = await client.GetAsync("/api/supplier/dashboard");

        await AssertOkAsync(response);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("totalJobs", out _));
        Assert.False(body.TryGetProperty("completedJobs", out _));
        Assert.False(body.TryGetProperty("upcomingJobs", out _));
    }

    [PostgresFact]
    public async Task SupplierJobAndQrCheckInRoutes_Removed_Return404()
    {
        var w = await SeedWorldAsync();
        var jobId = Guid.NewGuid();
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var job = new
        {
            description = "Pulizia",
            propertyAddress = "Via Roma 1",
            scheduledStartUtc = "2026-09-20T08:00:00Z",
            scheduledEndUtc = "2026-09-20T10:00:00Z",
            price = 50,
        };
        var checkIn = new { token = "0123456789ABCDEF0123456789ABCDEF" };

        var responses = new List<(string Route, HttpResponseMessage Response)>
        {
            ("GET /api/supplier/jobs", await supplier.GetAsync("/api/supplier/jobs")),
            ("POST /api/supplier/jobs", await supplier.PostAsJsonAsync("/api/supplier/jobs", job)),
            ("POST accept", await supplier.PostAsJsonAsync($"/api/supplier/jobs/{jobId}/accept", new { })),
            ("POST check-in", await supplier.PostAsJsonAsync($"/api/supplier/jobs/{jobId}/check-in", checkIn)),
            ("POST check-out", await supplier.PostAsJsonAsync($"/api/supplier/jobs/{jobId}/check-out", new { })),
            ("GET public", await anonymous.GetAsync($"/api/public/check-in/{jobId}?token={checkIn.token}")),
            ("POST public check-in", await anonymous.PostAsJsonAsync($"/api/public/check-in/{jobId}/check-in", checkIn)),
            ("POST public check-out", await anonymous.PostAsJsonAsync($"/api/public/check-in/{jobId}/check-out", checkIn)),
        };

        Assert.All(responses, r => Assert.True(
            r.Response.StatusCode == HttpStatusCode.NotFound,
            $"{r.Route}: expected 404, got {(int)r.Response.StatusCode}"));

        // Nothing is stored anywhere for the supplier: the table is gone (migration RemoveSupplierJobs).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tables = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'SupplierJobs'")
            .ToListAsync();
        Assert.Empty(tables);
    }

    // ─── Helpers ───

    private WebApplicationFactory<Program> WithClock(TimeProvider clock) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(clock);
        }));

    private static HttpClient CreateClient(WebApplicationFactory<Program> app, string userId, string roles)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return client;
    }

    private static async Task<JsonElement> GetKpisAsync(HttpClient client, string? period)
    {
        var url = period is null ? "/api/supplier/dashboard/kpis" : $"/api/supplier/dashboard/kpis?period={period}";
        var response = await client.GetAsync(url);
        await AssertOkAsync(response);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"Expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<Guid> CreateRequestAsync(HttpClient host, World w, Guid supplierOrgId)
    {
        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId,
            category = "cleaning",
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("id").GetGuid();
    }

    private static DateTime Utc(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static RequestSeed Request(
        Guid supplierOrgId,
        ServiceRequestStatus status,
        DateTime? completedAt = null,
        DateTime? updatedAt = null) => new(supplierOrgId, status, completedAt, updatedAt);

    private async Task AddRequestsAsync(World w, params RequestSeed[] seeds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
        foreach (var seed in seeds)
        {
            db.ServiceRequests.Add(new ServiceRequest
            {
                OrgId = w.HostOrgId,
                PropertyId = w.PropertyId,
                BookingId = w.BookingId,
                RentalContext = ServiceRequestRentalContext.ShortRent,
                SupplierOrgId = seed.SupplierOrgId,
                Category = "cleaning",
                Status = seed.Status,
                TakenAt = seed.Status is ServiceRequestStatus.Richiesto or ServiceRequestStatus.Rifiutato ? null : created,
                CompletedAt = seed.CompletedAt,
                PaidAt = seed.Status == ServiceRequestStatus.Pagato ? seed.CompletedAt?.AddDays(1) : null,
                RejectionReason = seed.Status == ServiceRequestStatus.Rifiutato ? "Non disponibile" : null,
                CreatedAt = created,
                UpdatedAt = seed.UpdatedAt ?? seed.CompletedAt ?? created,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<World> SeedWorldAsync()
    {
        const string comune = "H501";
        var ownerId = $"auth0|su11-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = "Casa SU11",
            Address = $"Via SU11 {Guid.NewGuid():N}",
            City = comune,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Ospite",
            Email = $"su11-{Guid.NewGuid():N}@example.com",
        };
        var today = TimeProvider.System.TodayInRome();
        var stay = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = today.AddDays(5),
            CheckOutDate = today.AddDays(8),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.AddRange(property, guest, stay);

        SupplierSeed AddSupplier(string name)
        {
            var supplierOrg = new OrgEntity
            {
                Name = $"SU11 {name}",
                Slug = $"su11-{name}-{Guid.NewGuid():N}"[..25],
                DisplayName = $"SU11 {name}",
                ContactEmail = $"su11-{name}-{Guid.NewGuid():N}@example.com",
                OrgType = OrgType.Supplier,
                PlanTier = PlanTier.Starter,
            };
            var member = new User
            {
                Id = $"auth0|su11-{name}-{Guid.NewGuid():N}",
                Email = $"{name}-{Guid.NewGuid():N}@example.com",
                FirstName = name,
                LastName = "Fornitore",
                OrgId = supplierOrg.Id,
                SupplierOrgId = supplierOrg.Id,
                IsActive = true,
            };
            var profile = new SupplierProfile
            {
                OrgId = supplierOrg.Id,
                Email = supplierOrg.ContactEmail,
                LegalName = $"SU11 {name} Srl",
                Phone = "+39 06 000000",
                Status = SupplierStatus.Active,
                ComuniJson = $"[\"{comune}\"]",
                CategoriesJson = "[\"cleaning\"]",
                TosAcceptedAt = DateTime.UtcNow,
            };
            db.AddRange(supplierOrg, member, profile);
            return new SupplierSeed(supplierOrg.Id, member.Id);
        }

        var supplierA = AddSupplier("alfa");
        var supplierB = AddSupplier("beta");
        await db.SaveChangesAsync();

        return new World(ownerId, org.Id, property.Id, stay.Id, supplierA, supplierB);
    }

    private sealed record SupplierSeed(Guid OrgId, string UserId);

    private sealed record World(
        string OwnerId,
        Guid HostOrgId,
        Guid PropertyId,
        Guid BookingId,
        SupplierSeed SupplierA,
        SupplierSeed SupplierB);

    private sealed record RequestSeed(
        Guid SupplierOrgId,
        ServiceRequestStatus Status,
        DateTime? CompletedAt,
        DateTime? UpdatedAt);
}
