using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-06 (R-01, A9-12, A2-15, A5-17, A7-05): date-only values sent by the web app
/// ("yyyy-MM-dd") must reach PostgreSQL <c>timestamptz</c> columns as UTC instead of
/// failing with "Cannot write DateTime with Kind=Unspecified" (HTTP 500).
/// Runs the real API on a throw-away PostgreSQL database with all migrations applied.
/// </summary>
public class DateTimeUtcNormalizationPostgresTests(PostgresDateTimeWebApplicationFactory factory)
    : IClassFixture<PostgresDateTimeWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task CalculateTouristTax_DateOnlyPayload_Returns200WithTax()
    {
        await factory.SeedTouristTaxRateAsync("Milano", 2.50m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
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

    [Fact]
    public async Task CalculateTouristTax_DateOnlyPayloadForComuneWithoutRate_Returns404()
    {
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

    [Fact]
    public async Task GetTouristTaxRateByCity_DateOnlyQuery_Returns200()
    {
        await factory.SeedTouristTaxRateAsync("Bologna", 3.00m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/tourist-tax-rates/city/Bologna?date=2026-10-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AdminTouristTaxRatePayload_DateOnlyValues_DeserializeAndPersistAsUtcMidnight()
    {
        // Same JSON options as the API; the admin create/update flow itself is tracked by A5-06 (CO-03).
        var jsonOptions = factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var rate = JsonSerializer.Deserialize<TouristTaxRate>(
            """{"city":"Torino","regionCode":"PIE","ratePerPersonPerNight":2.30,"effectiveFrom":"2026-04-01","effectiveTo":"2026-12-31"}""",
            jsonOptions)!;

        await using (var db = factory.CreateDbContext())
            await new TouristTaxRateRepository(db).AddAsync(rate);

        await using var read = factory.CreateDbContext();
        var stored = await read.TouristTaxRates.AsNoTracking().SingleAsync(r => r.Id == rate.Id);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveFrom);
        Assert.Equal(DateTimeKind.Utc, stored.EffectiveFrom.Kind);
        Assert.Equal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveTo);
    }

    [Fact]
    public async Task GetPricingHistory_DateOnlyFilters_Returns200WithEntriesOfBothBoundaryDays()
    {
        var owner = $"auth0|fd06-pricing-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        await factory.SeedPricingHistoryAtAsync(
            property.Id,
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

    [Fact]
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

        await using var db = factory.CreateDbContext();
        var lease = await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == id);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), lease.StartDate);
        Assert.Equal(new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.Equal(DateTimeKind.Utc, lease.StartDate.Kind);
    }

    [Fact]
    public async Task GetPublicAvailability_DateOnlyQuery_Returns200()
    {
        var property = await factory.SeedPropertyAsync($"auth0|fd06-avail-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/public/bookings/property/{property.Id}/availability?startDate=2026-10-01&endDate=2026-10-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetActiveByCityAsync_UnspecifiedKindParameter_QueriesTimestamptzWithoutError()
    {
        await factory.SeedTouristTaxRateAsync("Napoli", 2.00m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var db = factory.CreateDbContext();

        var rate = await new TouristTaxRateRepository(db).GetActiveByCityAsync(
            "Napoli",
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified));

        Assert.NotNull(rate);
    }

    [Fact]
    public async Task Query_DateMemberOfConvertedColumn_TranslatesOnPostgres()
    {
        var property = await factory.SeedPropertyAsync($"auth0|fd06-query-{Guid.NewGuid():N}");
        await factory.SeedPricingHistoryAtAsync(property.Id, new DateTime(2026, 9, 10, 2, 0, 0, DateTimeKind.Utc));
        await using var db = factory.CreateDbContext();
        var day = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Unspecified);

        var count = await db.PricingHistories
            .CountAsync(h => h.PropertyId == property.Id && h.AdaptationDate.Date <= day);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SaveChanges_UnspecifiedKindValue_IsStoredAsUtcAndReadBackAsUtc()
    {
        var id = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
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
            await db.SaveChangesAsync();
        }

        await using var read = factory.CreateDbContext();
        var stored = await read.TouristTaxRates.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveFrom);
        Assert.Equal(DateTimeKind.Utc, stored.EffectiveFrom.Kind);
        Assert.Equal(DateTimeKind.Utc, stored.EffectiveTo!.Value.Kind);
    }
}

/// <summary>
/// API factory backed by a throw-away PostgreSQL database with all EF migrations applied.
/// Uses TEST_POSTGRES_CONNECTION when set (database created and dropped per fixture),
/// otherwise a Testcontainers PostgreSQL instance.
/// </summary>
public sealed class PostgresDateTimeWebApplicationFactory : CasazenWebApplicationFactory, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        else
        {
            _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
            {
                Database = $"wt_fd06_{Guid.NewGuid():N}",
            }.ConnectionString;
        }

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_container is null)
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
        }

        await DisposeAsync();

        if (_container is not null)
            await _container.DisposeAsync();
    }

    public AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    public async Task SeedTouristTaxRateAsync(string city, decimal rate, DateTime effectiveFrom)
    {
        await using var db = CreateDbContext();
        if (await db.TouristTaxRates.AnyAsync(r => r.City == city))
            return;

        db.TouristTaxRates.Add(new TouristTaxRate
        {
            City = city,
            RegionCode = "XX",
            RatePerPersonPerNight = rate,
            MinimumAge = 14,
            IsActive = true,
            EffectiveFrom = effectiveFrom,
        });
        await db.SaveChangesAsync();
    }

    public async Task SeedPricingHistoryAtAsync(Guid propertyId, params DateTime[] adaptationDates)
    {
        await using var db = CreateDbContext();
        foreach (var date in adaptationDates)
        {
            db.PricingHistories.Add(new PricingHistory
            {
                PropertyId = propertyId,
                AdaptationDate = date,
                PreviousPrice = 100m,
                NewPrice = 110m,
                ChangeReason = "seasonal",
                OtasSynced = string.Empty,
                SyncStatus = "Synced",
                CreatedAt = date,
            });
        }

        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<DbContextOptions<AppDbContext>>(services);
            RemoveAllOf<IDbContextOptionsConfiguration<AppDbContext>>(services);
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(_connectionString));
        });
    }
}
