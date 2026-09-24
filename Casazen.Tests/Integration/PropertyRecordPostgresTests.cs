using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The property record over HTTP on PostgreSQL (PC-02): <c>PUT /api/properties/{id}</c> has PATCH semantics and never
/// resets a field it was not sent (A2-04); <c>GET /api/properties/{id}</c> is a light record without the bookings and
/// their check-in tokens (A2-32); another org's property is 404 for both.
/// </summary>
public class PropertyRecordPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PropertyRecordPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewOwner() => $"auth0|pc02-{Guid.NewGuid():N}";

    [PostgresFact]
    public async Task Update_OnlyNameSent_KeepsCleaningDepositRulesPolicyAndTimezone()
    {
        var owner = NewOwner();
        var (property, policyId) = await SeedPricedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}", new { name = "Solo il nome cambia" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await ReadStoredAsync(property.Id);
        Assert.Equal("Solo il nome cambia", stored.Name);
        Assert.Equal(60m, stored.CleaningFee);
        Assert.Equal(300m, stored.DamageDeposit);
        Assert.Equal("Niente feste dopo le 23", stored.HouseRules);
        Assert.Equal(policyId, stored.CancellationPolicyId);
        Assert.Equal("Europe/Vienna", stored.Timezone);
        Assert.Equal(property.NightlyRate, stored.NightlyRate);
        Assert.Equal(property.Address, stored.Address);
        Assert.True(stored.IsActive);
    }

    [PostgresFact]
    public async Task Update_FormFieldsRoundTrip_AppliesThemAndAllowsAStudio()
    {
        var owner = NewOwner();
        var (property, _) = await SeedPricedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}", new
        {
            name = "Monolocale",
            bedrooms = 0,
            bathrooms = 1,
            cleaningFee = 45.5m,
            damageDeposit = 0m,
            houseRules = "Check-in dalle 15",
            timezone = "Europe/Rome",
            cancellationPolicyId = (Guid?)null,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await ReadStoredAsync(property.Id);
        Assert.Equal(0, stored.Bedrooms);
        Assert.Equal(45.5m, stored.CleaningFee);
        Assert.Equal(0m, stored.DamageDeposit);
        Assert.Equal("Check-in dalle 15", stored.HouseRules);
        Assert.Equal("Europe/Rome", stored.Timezone);
        Assert.Null(stored.CancellationPolicyId);
    }

    [PostgresFact]
    public async Task Update_InvalidFields_Returns400ValidationErrorsByFieldAndChangesNothing()
    {
        var owner = NewOwner();
        var (property, _) = await SeedPricedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}", new
        {
            name = "Nome nuovo",
            bedrooms = -1,
            cleaningFee = -5m,
            timezone = "Mars/Olympus",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors");
        Assert.StartsWith("Le camere devono essere un numero intero tra 0 e 100", errors.GetProperty("Bedrooms")[0].GetString());
        Assert.True(errors.TryGetProperty("CleaningFee", out _));
        Assert.StartsWith("Fuso orario non valido", errors.GetProperty("Timezone")[0].GetString());

        var stored = await ReadStoredAsync(property.Id);
        Assert.Equal(property.Name, stored.Name);
        Assert.Equal(60m, stored.CleaningFee);
    }

    [PostgresFact]
    public async Task Update_UnknownCancellationPolicy_Returns422AndChangesNothing()
    {
        var owner = NewOwner();
        var (property, policyId) = await SeedPricedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}", new
        {
            name = "Nome nuovo",
            cancellationPolicyId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("cancellation_policy_not_found", problem.GetProperty("code").GetString());
        Assert.StartsWith("La policy di cancellazione scelta non esiste", problem.GetProperty("detail").GetString());

        var stored = await ReadStoredAsync(property.Id);
        Assert.Equal(property.Name, stored.Name);
        Assert.Equal(policyId, stored.CancellationPolicyId);
    }

    [PostgresFact]
    public async Task GetById_PropertyWithBookings_ReturnsRecordWithoutBookingsNorCheckInTokens()
    {
        var owner = NewOwner();
        var (property, policyId) = await SeedPricedPropertyAsync(owner);
        var checkInToken = await SeedBookingWithCheckInTokenAsync(property);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.GetAsync($"/api/properties/{property.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(checkInToken.ToString(), body, StringComparison.OrdinalIgnoreCase);
        var record = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.False(record.TryGetProperty("bookings", out _));
        Assert.False(record.TryGetProperty("otaIntegrations", out _));
        Assert.False(record.TryGetProperty("propertyDocuments", out _));
        Assert.False(record.TryGetProperty("safetyChecklistJson", out _));
        Assert.Equal(property.Id, record.GetProperty("id").GetGuid());
        Assert.Equal(60m, record.GetProperty("cleaningFee").GetDecimal());
        Assert.Equal(300m, record.GetProperty("damageDeposit").GetDecimal());
        Assert.Equal("Niente feste dopo le 23", record.GetProperty("houseRules").GetString());
        Assert.Equal("Europe/Vienna", record.GetProperty("timezone").GetString());
        Assert.Equal(policyId, record.GetProperty("cancellationPolicyId").GetGuid());
    }

    [PostgresFact]
    public async Task GetByIdAndUpdate_PropertyOfAnotherOrg_Return404AndChangeNothing()
    {
        var owner = NewOwner();
        var outsider = NewOwner();
        var (property, _) = await SeedPricedPropertyAsync(owner);
        await _factory.SeedOrgForOwnerAsync(outsider);
        using var client = _factory.CreateAuthenticatedClient(outsider, "PropertyOwner");

        var read = await client.GetAsync($"/api/properties/{property.Id}");
        var update = await client.PutAsJsonAsync($"/api/properties/{property.Id}", new { name = "Presa", cleaningFee = 0m });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        var stored = await ReadStoredAsync(property.Id);
        Assert.Equal(property.Name, stored.Name);
        Assert.Equal(60m, stored.CleaningFee);
    }

    [PostgresFact]
    public async Task GetCancellationPolicies_ReturnsTheCatalogByName()
    {
        var owner = NewOwner();
        var (_, policyId) = await SeedPricedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.GetAsync("/api/properties/cancellation-policies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policies = (await ReadJsonAsync(response)).EnumerateArray().ToList();
        var policy = Assert.Single(policies, p => p.GetProperty("id").GetGuid() == policyId);
        Assert.StartsWith("Flessibile", policy.GetProperty("name").GetString());
        Assert.Equal(24, policy.GetProperty("fullRefundHours").GetInt32());
    }

    private async Task<(Property Property, Guid PolicyId)> SeedPricedPropertyAsync(string owner)
    {
        var seeded = await _factory.SeedPropertyAsync(owner);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var policy = new CancellationPolicy
        {
            Name = $"Flessibile {Guid.NewGuid():N}"[..30],
            Description = "Rimborso totale fino a 24 ore prima dell'arrivo",
            FullRefundHours = 24,
            PartialRefundPercent = 50m,
            PartialRefundHours = 12,
        };
        db.CancellationPolicies.Add(policy);
        var stored = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == seeded.Id);
        stored.CleaningFee = 60m;
        stored.DamageDeposit = 300m;
        stored.HouseRules = "Niente feste dopo le 23";
        stored.Timezone = "Europe/Vienna";
        stored.CancellationPolicyId = policy.Id;
        await db.SaveChangesAsync();
        return (stored, policy.Id);
    }

    private async Task<Guid> SeedBookingWithCheckInTokenAsync(Property property)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Ada",
            LastName = "Ospite",
            Email = $"{Guid.NewGuid():N}@example.com",
            PhoneNumber = "+390000000000",
            Country = "Italy",
        };
        var token = Guid.NewGuid();
        db.Guests.Add(guest);
        db.Bookings.Add(new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(10),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(12),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
            CheckInToken = token,
            CheckInTokenExpiresAt = DateTime.UtcNow.AddDays(12),
        });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<Property> ReadStoredAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
}
