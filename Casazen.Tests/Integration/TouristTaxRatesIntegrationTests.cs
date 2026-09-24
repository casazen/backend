using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Admin tourist tax rates over HTTP on PostgreSQL (A5-06): create with a server-generated id, update and soft delete
/// with 404 on unknown ids, localized validation errors, and the rates seeded from the RS-7 research.
/// </summary>
public class TouristTaxRatesIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string AdminId = "auth0|tax-rates-admin";

    private readonly CasazenWebApplicationFactory _factory;

    public TouristTaxRatesIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task Create_DateOnlyPayloadWithClientId_Returns201WithServerIdAndStoresUtcMidnight()
    {
        var clientId = Guid.NewGuid();
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var response = await client.PostAsJsonAsync("/api/tourist-tax-rates", new
        {
            id = clientId,
            city = "Lecco",
            regionCode = "LOM",
            ratePerPersonPerNight = 1.50m,
            maxNights = 5,
            minimumAge = 14,
            effectiveFrom = "2027-01-01",
            effectiveTo = "2027-12-31",
            notes = "Test",
            sourceUrl = "https://www.comune.lecco.it/imposta-di-soggiorno",
            verificationLevel = "Official",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.NotEqual(clientId, id);
        Assert.Equal("Official", body.GetProperty("verificationLevel").GetString());

        var stored = await ReadRateAsync(id);
        Assert.NotNull(stored);
        Assert.Equal("Lecco", stored!.City);
        Assert.Equal(1.50m, stored.RatePerPersonPerNight);
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveFrom);
        Assert.Equal(new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveTo);
        Assert.Equal("https://www.comune.lecco.it/imposta-di-soggiorno", stored.SourceUrl);
        Assert.Equal(TouristTaxRateVerification.Official, stored.VerificationLevel);
        Assert.True(stored.IsActive);

        var get = await client.GetAsync($"/api/tourist-tax-rates/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [PostgresFact]
    public async Task Create_AsHost_Returns403AndStoresNothing()
    {
        using var client = _factory.CreateAuthenticatedClient($"auth0|tax-host-{Guid.NewGuid():N}", "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/tourist-tax-rates", ValidPayload("Host Town"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await WithDbAsync(db => db.TouristTaxRates.AnyAsync(r => r.City == "Host Town")));
    }

    [PostgresFact]
    public async Task Create_InvalidPayload_Returns400WithLocalizedFieldErrors()
    {
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var response = await client.PostAsJsonAsync("/api/tourist-tax-rates", new
        {
            city = "Invalid Town",
            regionCode = "LOM",
            ratePerPersonPerNight = 2m,
            minimumAge = 14,
            effectiveFrom = "2027-06-01",
            effectiveTo = "2027-01-01",
            sourceUrl = "javascript:alert(1)",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors");
        Assert.StartsWith("La data di fine validità", errors.GetProperty("EffectiveTo")[0].GetString());
        Assert.StartsWith("La fonte deve essere un indirizzo web", errors.GetProperty("SourceUrl")[0].GetString());
        Assert.False(await WithDbAsync(db => db.TouristTaxRates.AnyAsync(r => r.City == "Invalid Town")));
    }

    [PostgresFact]
    public async Task Create_MissingRequiredFieldsInEnglish_Returns400WithEnglishMessages()
    {
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var response = await client.PostAsJsonAsync("/api/tourist-tax-rates", new
        {
            city = "",
            ratePerPersonPerNight = 0m,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await ReadJsonAsync(response)).GetProperty("errors");
        Assert.Equal("Enter the municipality of the rate.", errors.GetProperty("City")[0].GetString());
        Assert.StartsWith("The rate per person per night", errors.GetProperty("RatePerPersonPerNight")[0].GetString());
        Assert.StartsWith("Enter the age", errors.GetProperty("MinimumAge")[0].GetString());
        Assert.StartsWith("Enter the date", errors.GetProperty("EffectiveFrom")[0].GetString());
    }

    [PostgresFact]
    public async Task Update_ExistingRate_Returns200AndPersistsEveryField()
    {
        var id = await CreateRateAsync("Update Town");
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var response = await client.PutAsJsonAsync($"/api/tourist-tax-rates/{id}", new
        {
            city = "Update Town",
            regionCode = "LOM",
            ratePerPersonPerNight = 4.20m,
            maxNights = null as int?,
            minimumAge = 12,
            effectiveFrom = "2027-03-01",
            notes = "Aggiornata",
            sourceUrl = "https://www.example.it/delibera.pdf",
            verificationLevel = "ThirdParty",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, (await ReadJsonAsync(response)).GetProperty("id").GetGuid());

        var stored = await ReadRateAsync(id);
        Assert.Equal(4.20m, stored!.RatePerPersonPerNight);
        Assert.Null(stored.MaxNights);
        Assert.Equal(12, stored.MinimumAge);
        Assert.Equal(new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc), stored.EffectiveFrom);
        Assert.Null(stored.EffectiveTo);
        Assert.Equal("Aggiornata", stored.Notes);
        Assert.Equal("https://www.example.it/delibera.pdf", stored.SourceUrl);
        Assert.Equal(TouristTaxRateVerification.ThirdParty, stored.VerificationLevel);
        Assert.Equal(1, await WithDbAsync(db => db.TouristTaxRates.CountAsync(r => r.City == "Update Town")));
    }

    [PostgresFact]
    public async Task Update_UnknownId_Returns404WithCodeAndCreatesNothing()
    {
        var unknownId = Guid.NewGuid();
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var response = await client.PutAsJsonAsync($"/api/tourist-tax-rates/{unknownId}", ValidPayload("Ghost Town"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("tourist_tax_rate_not_found", problem.GetProperty("code").GetString());
        Assert.Equal("Tariffa dell'imposta di soggiorno non trovata.", problem.GetProperty("detail").GetString());
        Assert.Null(await ReadRateAsync(unknownId));
        Assert.False(await WithDbAsync(db => db.TouristTaxRates.AnyAsync(r => r.City == "Ghost Town")));
    }

    [PostgresFact]
    public async Task Delete_ExistingRate_Returns204AndRemovesItFromTheActiveList()
    {
        var id = await CreateRateAsync("Delete Town");
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var response = await client.DeleteAsync($"/api/tourist-tax-rates/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await ReadRateAsync(id);
        Assert.NotNull(stored);
        Assert.False(stored!.IsActive);

        var list = await ReadJsonAsync(await client.GetAsync("/api/tourist-tax-rates"));
        Assert.DoesNotContain(list.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
    }

    [PostgresFact]
    public async Task Delete_UnknownId_Returns404()
    {
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var response = await client.DeleteAsync($"/api/tourist-tax-rates/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tourist_tax_rate_not_found", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task GetAll_AfterMigrations_ReturnsOnlyTheSeededRepresentableRatesWithSource()
    {
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var list = await ReadJsonAsync(await client.GetAsync("/api/tourist-tax-rates"));

        var seededIds = TouristTaxRateSeed.BuildRates().Select(r => r.Id).ToHashSet();
        var seeded = list.EnumerateArray().Where(r => seededIds.Contains(r.GetProperty("id").GetGuid())).ToList();
        Assert.Equal(
            new[] { "Como", "Firenze", "Milano", "Napoli" },
            seeded.Select(r => r.GetProperty("city").GetString()!).Order().ToArray());
        Assert.All(seeded, r =>
        {
            Assert.StartsWith("https://", r.GetProperty("sourceUrl").GetString());
            Assert.Equal("Official", r.GetProperty("verificationLevel").GetString());
        });

        var milano = seeded.Single(r => r.GetProperty("city").GetString() == "Milano");
        Assert.Equal(9.50m, milano.GetProperty("ratePerPersonPerNight").GetDecimal());
        Assert.Equal(14, milano.GetProperty("maxNights").GetInt32());
        Assert.Equal(18, milano.GetProperty("minimumAge").GetInt32());

        Assert.All(seeded, r => Assert.Equal(TouristTaxRateSeed.IstatCodes[r.GetProperty("city").GetString()!], r.GetProperty("istatCode").GetString()));

        // BK-03: Roma by category and Venezia by cadastral group and season, exactly the official rows.
        var categoryIds = TouristTaxRateSeed.BuildCategoryAndSeasonRates().Select(r => r.Id).Order().ToArray();
        var storedCategoryRates = await WithDbAsync(db => db.TouristTaxRates
            .Where(r => r.City == "Roma" || r.City == "Venezia")
            .Select(r => r.Id)
            .ToListAsync());
        Assert.Equal(categoryIds, storedCategoryRates.Order().ToArray());

        // Still not loaded: percentage per person from third parties (Bologna), third-party amount (Torino), no rate.
        var notLoaded = new[] { "Bologna", "Torino", "Seveso", "Cesano Maderno" };
        Assert.False(await WithDbAsync(db => db.TouristTaxRates.AnyAsync(r => notLoaded.Contains(r.City))));
    }

    [PostgresFact]
    public async Task Create_PercentageRateWithSeasonAndCategory_StoresTheNewFields()
    {
        using var admin = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var response = await admin.PostAsJsonAsync("/api/tourist-tax-rates", new
        {
            city = "Rimini",
            regionCode = "EMR",
            istatCode = "099014",
            accommodationCategory = "Appartamenti",
            seasonStart = "06-01",
            seasonEnd = "09-30",
            calculationMethod = "PercentOfNightlyPrice",
            ratePerPersonPerNight = 0,
            percentOfNightlyPrice = 5.5m,
            capPerPersonPerNight = 3.00m,
            maxNights = 7,
            minimumAge = 14,
            effectiveFrom = "2027-01-01",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await ReadRateAsync((await ReadJsonAsync(response)).GetProperty("id").GetGuid());
        Assert.NotNull(stored);
        Assert.Equal("099014", stored!.IstatCode);
        Assert.Equal("Appartamenti", stored.AccommodationCategory);
        Assert.Equal(("06-01", "09-30"), (stored.SeasonStart, stored.SeasonEnd));
        Assert.Equal(TouristTaxCalculationMethod.PercentOfNightlyPrice, stored.CalculationMethod);
        Assert.Equal(5.5m, stored.PercentOfNightlyPrice);
        Assert.Equal(3.00m, stored.CapPerPersonPerNight);
        Assert.Equal(0m, stored.RatePerPersonPerNight);
    }

    [PostgresFact]
    public async Task Create_SeasonWithOneBoundOrMinimumAgeOver18_Returns400WithKeys()
    {
        using var admin = _factory.CreateAuthenticatedClient(AdminId, "Admin");

        var response = await admin.PostAsJsonAsync("/api/tourist-tax-rates", new
        {
            city = "Season Town",
            regionCode = "LOM",
            ratePerPersonPerNight = 2.00m,
            minimumAge = 30,
            seasonStart = "02-30",
            effectiveFrom = "2027-01-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await WithDbAsync(db => db.TouristTaxRates.AnyAsync(r => r.City == "Season Town")));
    }

    private static object ValidPayload(string city) => new
    {
        city,
        regionCode = "LOM",
        ratePerPersonPerNight = 2.00m,
        maxNights = 7,
        minimumAge = 14,
        effectiveFrom = "2027-01-01",
    };

    private async Task<Guid> CreateRateAsync(string city)
    {
        using var client = _factory.CreateAuthenticatedClient(AdminId, "Admin");
        var response = await client.PostAsJsonAsync("/api/tourist-tax-rates", ValidPayload(city));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private Task<TouristTaxRate?> ReadRateAsync(Guid id) =>
        WithDbAsync(db => db.TouristTaxRates.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id));

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
