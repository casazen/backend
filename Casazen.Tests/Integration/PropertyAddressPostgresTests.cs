using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-06 (A2-19, A2-33) over HTTP on PostgreSQL: the address is unique per org and per unit (several apartments of one
/// building are possible), the database index is the guarantee under concurrency (23505 → 409, never a check before the
/// insert), another org's property never conflicts nor is revealed, and the coordinates are validated and kept with six
/// decimals.
/// </summary>
public class PropertyAddressPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string DuplicateCode = "duplicate_property_address";

    private readonly CasazenWebApplicationFactory _factory;

    public PropertyAddressPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private async Task<(string UserId, HttpClient Client)> NewHostAsync()
    {
        var userId = $"auth0|addr-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(userId);
        // Starter allows 3 properties; these tests create more apartments in one building.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
            stored.PlanTier = PlanTier.Pro;
            stored.SubscriptionId = $"sub_test_{org.Id:N}";
            stored.SubscriptionStatus = SubscriptionStatus.Active;
            await db.SaveChangesAsync();
        }

        return (userId, _factory.CreateAuthenticatedClient(userId, "PropertyOwner"));
    }

    // ─── Several apartments in one building ──────────────────────────────────────

    [PostgresFact]
    public async Task Create_SecondApartmentOfTheSameBuilding_WithAnotherUnit_Succeeds()
    {
        var (_, host) = await NewHostAsync();
        var street = NewStreet();

        var first = await host.PostAsJsonAsync("/api/properties", Body(street, unit: "int. 1"));
        var second = await host.PostAsJsonAsync("/api/properties", Body(street, unit: "int. 2"));
        var third = await host.PostAsJsonAsync("/api/properties", Body(street, unit: "Scala B int. 1"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        var id = (await ReadAsync(second)).GetProperty("id").GetGuid();
        var record = await ReadAsync(await host.GetAsync($"/api/properties/{id}"));
        Assert.Equal("int. 2", record.GetProperty("unit").GetString());
    }

    [PostgresFact]
    public async Task Create_SameAddressAndUnitInTheSameOrg_Returns409WithTheStableCode()
    {
        var (_, host) = await NewHostAsync();
        var street = NewStreet();
        Assert.Equal(HttpStatusCode.Created, (await host.PostAsJsonAsync("/api/properties", Body(street, unit: "int. 1"))).StatusCode);

        var duplicate = await host.PostAsJsonAsync("/api/properties", Body(street, unit: "int. 1"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(DuplicateCode, await CodeOfAsync(duplicate));
    }

    [PostgresFact]
    public async Task Create_SameAddressWithoutAUnitTwice_Returns409()
    {
        var (_, host) = await NewHostAsync();
        var street = NewStreet();
        Assert.Equal(HttpStatusCode.Created, (await host.PostAsJsonAsync("/api/properties", Body(street))).StatusCode);

        var duplicate = await host.PostAsJsonAsync("/api/properties", Body(street, unit: "   "));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(DuplicateCode, await CodeOfAsync(duplicate));
    }

    [PostgresFact]
    public async Task Create_AddressWrittenWithAnotherCaseAndSpaces_IsTheSameAddress()
    {
        var (_, host) = await NewHostAsync();
        var street = NewStreet();
        Assert.Equal(HttpStatusCode.Created, (await host.PostAsJsonAsync("/api/properties", Body(street, unit: "Int. 5"))).StatusCode);

        var variant = await host.PostAsJsonAsync(
            "/api/properties",
            Body($"  {street.ToUpperInvariant().Replace(" ", "   ")} ", city: "  MILANO ", unit: "int.   5"));

        Assert.Equal(HttpStatusCode.Conflict, variant.StatusCode);
        Assert.Equal(DuplicateCode, await CodeOfAsync(variant));
    }

    // ─── Per org ─────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Create_SameAddressInAnotherOrg_Succeeds_SoNothingOfTheOtherTenantIsRevealed()
    {
        var (_, hostA) = await NewHostAsync();
        var (_, hostB) = await NewHostAsync();
        var street = NewStreet();
        Assert.Equal(HttpStatusCode.Created, (await hostA.PostAsJsonAsync("/api/properties", Body(street))).StatusCode);

        var other = await hostB.PostAsJsonAsync("/api/properties", Body(street));

        // Before PC-06 this was a 409 "address already used": the second host learned that the building is in use.
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    // ─── Concurrency: the database index decides ─────────────────────────────────

    [PostgresFact]
    public async Task Create_ParallelRequestsForTheSameAddressAndUnit_CreateExactlyOne()
    {
        var (userId, host) = await NewHostAsync();
        var street = NewStreet();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.PostAsJsonAsync("/api/properties", Body(street, unit: "int. 7"))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var rejected = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(5, rejected.Count);
        foreach (var response in rejected)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(DuplicateCode, await CodeOfAsync(response));
        }

        Assert.Equal(1, await CountActiveAsync(userId, street));
    }

    [PostgresFact]
    public async Task Create_ParallelRequestsForDifferentUnitsOfTheSameBuilding_CreateAll()
    {
        var (userId, host) = await NewHostAsync();
        var street = NewStreet();

        var responses = await Task.WhenAll(Enumerable.Range(1, 5).Select(n => host.PostAsJsonAsync("/api/properties", Body(street, unit: $"int. {n}"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(5, await CountActiveAsync(userId, street));
    }

    [PostgresFact]
    public async Task Update_ParallelChangesOfTwoPropertiesToTheSameAddress_OnlyOneWinsTheRestIsAConflict()
    {
        // Updates are not serialized by the org lock: this is the real 23505 path, with no check before the save.
        var (_, host) = await NewHostAsync();
        var first = await CreateAsync(host, Body(NewStreet()));
        var second = await CreateAsync(host, Body(NewStreet()));
        var target = NewStreet();

        var responses = await Task.WhenAll(
            host.PutAsJsonAsync($"/api/properties/{first}", new { address = target, unit = "int. 3" }),
            host.PutAsJsonAsync($"/api/properties/{second}", new { address = target, unit = "int. 3" }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent);
        var loser = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal(DuplicateCode, await CodeOfAsync(loser));
    }

    [PostgresFact]
    public async Task Update_ToTheAddressOfAnotherPropertyOfTheOrg_Returns409UntilTheUnitDiffers()
    {
        var (_, host) = await NewHostAsync();
        var street = NewStreet();
        await CreateAsync(host, Body(street, unit: "int. 1"));
        var other = await CreateAsync(host, Body(NewStreet()));

        var clash = await host.PutAsJsonAsync($"/api/properties/{other}", new { address = street, unit = "int. 1" });
        var distinct = await host.PutAsJsonAsync($"/api/properties/{other}", new { address = street, unit = "int. 2" });

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(DuplicateCode, await CodeOfAsync(clash));
        Assert.Equal(HttpStatusCode.NoContent, distinct.StatusCode);
    }

    [PostgresFact]
    public async Task Update_RewritingTheOwnAddressAndUnit_IsNotAConflictWithItself()
    {
        var (_, host) = await NewHostAsync();
        var street = NewStreet();
        var id = await CreateAsync(host, Body(street, unit: "int. 1"));

        var response = await host.PutAsJsonAsync($"/api/properties/{id}", new { address = street, unit = "INT. 1", name = "Nuovo nome" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [PostgresFact]
    public async Task Update_ClearingTheUnit_StoresNullAndTakesTheBareAddress()
    {
        var (_, host) = await NewHostAsync();
        var street = NewStreet();
        var id = await CreateAsync(host, Body(street, unit: "int. 1"));

        Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsJsonAsync($"/api/properties/{id}", new { unit = (string?)null })).StatusCode);

        var record = await ReadAsync(await host.GetAsync($"/api/properties/{id}"));
        Assert.True(!record.TryGetProperty("unit", out var unit) || unit.ValueKind == JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsJsonAsync("/api/properties", Body(street))).StatusCode);
    }

    [PostgresFact]
    public async Task Create_AddressOfADeletedProperty_IsFreeAgain()
    {
        var (userId, host) = await NewHostAsync();
        var street = NewStreet();
        var id = await CreateAsync(host, Body(street));
        await MarkDeletedAsync(id);

        var again = await host.PostAsJsonAsync("/api/properties", Body(street));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(1, await CountActiveAsync(userId, street));
    }

    [PostgresFact]
    public async Task Create_AddressOfAPausedProperty_StaysTaken()
    {
        var (_, host) = await NewHostAsync();
        var street = NewStreet();
        var id = await CreateAsync(host, Body(street));
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync($"/api/properties/{id}/pause", null)).StatusCode);

        var again = await host.PostAsJsonAsync("/api/properties", Body(street));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // ─── Unit ────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Create_UnitWithExtraSpaces_IsStoredTrimmedAndCollapsed()
    {
        var (_, host) = await NewHostAsync();

        var id = await CreateAsync(host, Body(NewStreet(), unit: "  Scala   B  int. 5 "));

        var record = await ReadAsync(await host.GetAsync($"/api/properties/{id}"));
        Assert.Equal("Scala B int. 5", record.GetProperty("unit").GetString());
    }

    [PostgresFact]
    public async Task Create_UnitLongerThanTheLimit_Returns400()
    {
        var (_, host) = await NewHostAsync();

        var response = await host.PostAsJsonAsync("/api/properties", Body(NewStreet(), unit: new string('x', 31)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── Coordinates ─────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Create_CoordinatesWithSixDecimals_AreStoredAndReturnedAsSent()
    {
        var (_, host) = await NewHostAsync();

        var id = await CreateAsync(host, Body(NewStreet(), latitude: 45.464211m, longitude: 9.189982m));

        // numeric(18,2) used to turn 45.464211 into 45.46 (about 1 km away on the map).
        var record = await ReadAsync(await host.GetAsync($"/api/properties/{id}"));
        Assert.Equal(45.464211m, record.GetProperty("latitude").GetDecimal());
        Assert.Equal(9.189982m, record.GetProperty("longitude").GetDecimal());
        Assert.Equal((45.464211m, 9.189982m), await CoordinatesInDbAsync(id));
    }

    [PostgresFact]
    public async Task Create_CoordinatesWithMoreThanSixDecimals_AreRoundedToSix()
    {
        var (_, host) = await NewHostAsync();

        var id = await CreateAsync(host, Body(NewStreet(), latitude: 45.46421149m, longitude: -9.18998251m));

        Assert.Equal((45.464211m, -9.189983m), await CoordinatesInDbAsync(id));
    }

    [PostgresTheory]
    [InlineData(90.5, 9.0)]
    [InlineData(-91, 9.0)]
    [InlineData(45.0, 180.5)]
    [InlineData(45.0, -181)]
    [InlineData(4500.5, 9.0)]
    public async Task Create_CoordinateOutOfRange_Returns400AndCreatesNothing(double latitude, double longitude)
    {
        var (userId, host) = await NewHostAsync();
        var street = NewStreet();

        var response = await host.PostAsJsonAsync("/api/properties", Body(street, latitude: (decimal)latitude, longitude: (decimal)longitude));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountActiveAsync(userId, street));
    }

    [PostgresFact]
    public async Task Update_CoordinateOutOfRange_Returns400AndKeepsTheStoredOnes()
    {
        var (_, host) = await NewHostAsync();
        var id = await CreateAsync(host, Body(NewStreet(), latitude: 41.9m, longitude: 12.5m));

        var response = await host.PutAsJsonAsync($"/api/properties/{id}", new { latitude = 120m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((41.9m, 12.5m), await CoordinatesInDbAsync(id));
    }

    [PostgresFact]
    public async Task Update_PreciseCoordinates_AreStoredWithSixDecimals()
    {
        var (_, host) = await NewHostAsync();
        var id = await CreateAsync(host, Body(NewStreet()));

        var response = await host.PutAsJsonAsync($"/api/properties/{id}", new { latitude = 41.902782m, longitude = 12.496366m });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal((41.902782m, 12.496366m), await CoordinatesInDbAsync(id));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static string NewStreet() => $"Via Roma {Guid.NewGuid():N}";

    private static object Body(
        string address,
        string city = "Milano",
        string? unit = null,
        decimal latitude = 0m,
        decimal longitude = 0m) => new
        {
            name = "Appartamento",
            description = "Appartamento di prova per l'indirizzo",
            address,
            unit,
            city,
            postalCode = "20100",
            latitude,
            longitude,
            bedrooms = 1,
            bathrooms = 1,
            maxGuests = 2,
            nightlyRate = 90,
        };

    private static async Task<Guid> CreateAsync(HttpClient host, object body)
    {
        var response = await host.PostAsJsonAsync("/api/properties", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<int> CountActiveAsync(string ownerId, string street)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = street.Trim();
        return await db.Properties.IgnoreQueryFilters()
            .CountAsync(p => p.OwnerId == ownerId && p.Address.ToLower() == key.ToLower() && p.IsActive && !p.IsDeleted);
    }

    private async Task MarkDeletedAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        property.IsDeleted = true;
        property.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<(decimal Latitude, decimal Longitude)> CoordinatesInDbAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
        return (property.Latitude, property.Longitude);
    }
}
