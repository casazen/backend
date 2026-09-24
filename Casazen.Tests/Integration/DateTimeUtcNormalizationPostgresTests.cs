using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-06 (R-01, A9-12, A2-15, A5-17, A7-05): date-only values sent by the web app
/// ("yyyy-MM-dd") must reach PostgreSQL <c>timestamptz</c> columns as UTC instead of
/// failing with "Cannot write DateTime with Kind=Unspecified" (HTTP 500).
/// Runs the real API on the factory's migrated PostgreSQL database (FD-04).
/// </summary>
public class DateTimeUtcNormalizationPostgresTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [PostgresFact]
    public async Task CalculateTouristTax_DateOnlyPayload_Returns200WithTax()
    {
        await SeedTouristTaxRateAsync("Milano", 2.50m);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/public/tourist-tax/calculate", new
        {
            comuneSlug = "milano",
            numberOfAdults = 2,
            numberOfChildren = 0,
            checkInDate = "2026-10-01",
            checkOutDate = "2026-10-04",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(3, body.GetProperty("nights").GetInt32());
        Assert.Equal(15.00m, body.GetProperty("taxAmount").GetDecimal());
    }

    [PostgresFact]
    public async Task CalculateTouristTax_DateOnlyPayloadForComuneWithoutRate_Returns404()
    {
        await WithDbAsync(db => db.TouristTaxRates.Where(r => r.City == "Firenze").ExecuteDeleteAsync());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/public/tourist-tax/calculate", new
        {
            comuneSlug = "firenze",
            numberOfAdults = 2,
            numberOfChildren = 0,
            checkInDate = "2026-10-01",
            checkOutDate = "2026-10-04",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [PostgresFact]
    public async Task GetTouristTaxRateByCity_DateOnlyQuery_Returns200()
    {
        await SeedTouristTaxRateAsync("Bologna", 3.00m);
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/tourist-tax-rates/city/Bologna?date=2026-10-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task AdminTouristTaxRatePayload_DateOnlyValues_DeserializeAndPersistAsUtcMidnight()
    {
        // Same JSON options as the API; the admin create/update flow itself is tracked by A5-06 (CO-03).
        var jsonOptions = factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var rate = JsonSerializer.Deserialize<TouristTaxRate>(
            """{"city":"Torino","regionCode":"PIE","ratePerPersonPerNight":2.30,"effectiveFrom":"2026-04-01","effectiveTo":"2026-12-31"}""",
            jsonOptions)!;

        await WithDbAsync(db => new TouristTaxRateRepository(db).AddAsync(rate));

        var stored = await WithDbAsync(db => db.TouristTaxRates.AsNoTracking().SingleAsync(r => r.Id == rate.Id));
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveFrom);
        Assert.Equal(DateTimeKind.Utc, stored.EffectiveFrom.Kind);
        Assert.Equal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveTo);
    }

    [PostgresFact]
    public async Task GetPricingHistory_DateOnlyFilters_Returns200WithEntriesOfBothBoundaryDays()
    {
        var owner = $"auth0|fd06-pricing-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        await SeedPricingHistoryAtAsync(
            property,
            new DateTime(2026, 8, 31, 2, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 10, 2, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 11, 2, 0, 0, DateTimeKind.Utc));
        using var client = factory.CreateAuthenticatedClient(owner);

        var response = await client.GetAsync(
            $"/api/pricing-adapter/history/{property.Id}?from=2026-09-01&to=2026-09-10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var dates = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("adaptationDate").GetDateTime().ToUniversalTime().Date)
            .OrderBy(d => d)
            .ToList();
        Assert.Equal([new DateTime(2026, 9, 1), new DateTime(2026, 9, 10)], dates);
    }

    [PostgresFact]
    public async Task CreateLease_DateOnlyPayload_Returns201AndStoresUtcMidnight()
    {
        var owner = $"auth0|fd06-lease-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");

        var response = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId = property.Id,
            fiscalRegime = "CedolareSecca",
            startDate = "2026-09-01",
            endDate = "2030-08-31",
            monthlyRent = 1200m,
            parties = new object[]
            {
                new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501Z", citizenship = "IT", contactEmail = "mario@example.com" },
                new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205X", citizenship = "IT", contactEmail = "giulia@example.com" },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("id").GetGuid();

        var lease = await WithDbAsync(db => db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == id));
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), lease.StartDate);
        Assert.Equal(new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.Equal(DateTimeKind.Utc, lease.StartDate.Kind);
    }

    [PostgresFact]
    public async Task GetPublicAvailability_DateOnlyQuery_Returns200()
    {
        var property = await factory.SeedPropertyAsync($"auth0|fd06-avail-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/public/bookings/property/{property.Id}/availability?startDate=2026-10-01&endDate=2026-10-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task GetActiveByCityAsync_UnspecifiedKindParameter_QueriesTimestamptzWithoutError()
    {
        await SeedTouristTaxRateAsync("Napoli", 2.00m);

        var rate = await WithDbAsync(db => new TouristTaxRateRepository(db).GetActiveByCityAsync(
            "Napoli",
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified)));

        Assert.NotNull(rate);
    }

    [PostgresFact]
    public async Task Query_DateMemberOfConvertedColumn_TranslatesOnPostgres()
    {
        var property = await factory.SeedPropertyAsync($"auth0|fd06-query-{Guid.NewGuid():N}");
        await SeedPricingHistoryAtAsync(property, new DateTime(2026, 9, 10, 2, 0, 0, DateTimeKind.Utc));
        var day = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Unspecified);

        var count = await WithDbAsync(db => db.PricingHistories
            .CountAsync(h => h.PropertyId == property.Id && h.AdaptationDate.Date <= day));

        Assert.Equal(1, count);
    }

    [PostgresFact]
    public async Task SaveChanges_UnspecifiedKindValue_IsStoredAsUtcAndReadBackAsUtc()
    {
        var id = Guid.NewGuid();
        await WithDbAsync(db =>
        {
            db.TouristTaxRates.Add(new TouristTaxRate
            {
                Id = id,
                City = "Genova",
                RegionCode = "LIG",
                RatePerPersonPerNight = 1.50m,
                EffectiveFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Unspecified),
                EffectiveTo = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Unspecified),
            });
            return db.SaveChangesAsync();
        });

        var stored = await WithDbAsync(db => db.TouristTaxRates.AsNoTracking().SingleAsync(r => r.Id == id));
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveFrom);
        Assert.Equal(DateTimeKind.Utc, stored.EffectiveFrom.Kind);
        Assert.Equal(DateTimeKind.Utc, stored.EffectiveTo!.Value.Kind);
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task SeedTouristTaxRateAsync(string city, decimal rate) =>
        WithDbAsync(async db =>
        {
            if (await db.TouristTaxRates.AnyAsync(r => r.City == city))
                return 0;

            db.TouristTaxRates.Add(new TouristTaxRate
            {
                City = city,
                RegionCode = "XX",
                RatePerPersonPerNight = rate,
                MinimumAge = 14,
                IsActive = true,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            return await db.SaveChangesAsync();
        });

    private Task SeedPricingHistoryAtAsync(Property property, params DateTime[] adaptationDates) =>
        WithDbAsync(db =>
        {
            foreach (var date in adaptationDates)
            {
                db.PricingHistories.Add(new PricingHistory
                {
                    PropertyId = property.Id,
                    OrgId = property.OrgId,
                    AdaptationDate = date,
                    PreviousPrice = 100m,
                    NewPrice = 110m,
                    ChangeReason = "seasonal",
                    SyncStatus = "Synced",
                    CreatedAt = date,
                });
            }

            return db.SaveChangesAsync();
        });
}
