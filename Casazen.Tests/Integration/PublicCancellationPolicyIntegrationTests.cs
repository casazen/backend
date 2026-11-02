using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-03b over HTTP: the cancellation policy of a property as the public page reads it, and the dates of a stay's quote. The last day of the
/// full refund and of the partial one is the last whole day that ends at least that many hours before the start of the check-in day
/// (Europe/Rome, daylight saving included), on the policy of the stay (the catalog policy with what the host changed of it); a window the
/// policy does not have is null, never a number that means «never». These dates are what a guest is promised, so the oracle here counts
/// instants and does not call the function under test.
/// </summary>
public class PublicCancellationPolicyIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PublicCancellationPolicyIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PublicPolicyAndQuoteDates_FollowThePolicyOfTheStay_AndSayNothingOfAWindowThatDoesNotExist()
    {
        // «Contenuta»: full refund 14 days before (336 h), 50 % up to 7 days before (168 h). Three houses of one org: the catalog policy as it
        // is, the same policy with the host's full-refund window moved to 20 days, and the same policy marked non-refundable by the host.
        var org = await SeedOrgAsync();
        var asIs = await SeedHouseAsync(org, p => p.CancellationPolicy = CatalogPolicies.Get(CatalogPolicies.Contenuta));
        var moved = await SeedHouseAsync(org, p =>
        {
            p.CancellationPolicy = CatalogPolicies.Get(CatalogPolicies.Contenuta);
            p.CancellationFullRefundHours = 480;
        });
        var never = await SeedHouseAsync(org, p =>
        {
            p.CancellationPolicy = CatalogPolicies.Get(CatalogPolicies.Contenuta);
            p.CancellationRefundType = HostCancellationRefundType.NonRefundable;
        });
        using var client = _factory.CreateClient();
        var checkIn = TimeProvider.System.TodayInRome().AddDays(60);

        // The policy on the public page of each house.
        var policyAsIs = (await GetPublicHouseAsync(client, org.Slug, asIs.Slug!)).GetProperty("cancellationPolicy");
        Assert.Equal((336, 168, 50m, false), (
            policyAsIs.GetProperty("fullRefundHours").GetInt32(),
            policyAsIs.GetProperty("partialRefundHours").GetInt32(),
            policyAsIs.GetProperty("partialRefundPercent").GetDecimal(),
            policyAsIs.GetProperty("hostOverrideApplies").GetBoolean()));
        Assert.Equal(28, policyAsIs.GetProperty("longStayNights").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, policyAsIs.GetProperty("description").ValueKind);

        var policyMoved = (await GetPublicHouseAsync(client, org.Slug, moved.Slug!)).GetProperty("cancellationPolicy");
        Assert.Equal(480, policyMoved.GetProperty("fullRefundHours").GetInt32());
        Assert.True(policyMoved.GetProperty("hostOverrideApplies").GetBoolean());
        // The catalog text would say something else once the host moved the window: it is not given.
        Assert.Equal(JsonValueKind.Null, policyMoved.GetProperty("description").ValueKind);

        var policyNever = (await GetPublicHouseAsync(client, org.Slug, never.Slug!)).GetProperty("cancellationPolicy");
        Assert.Equal(JsonValueKind.Null, policyNever.GetProperty("fullRefundHours").ValueKind);
        Assert.Equal(JsonValueKind.Null, policyNever.GetProperty("partialRefundHours").ValueKind);
        Assert.Equal(JsonValueKind.Null, policyNever.GetProperty("partialRefundPercent").ValueKind);

        // The dates of the quote of a three-night stay on each.
        var quoteAsIs = (await QuoteAsync(client, asIs.Id, checkIn)).GetProperty("cancellation");
        Assert.Equal(LastWholeDay(checkIn, 336), quoteAsIs.GetProperty("fullRefundUntil").GetString());
        Assert.Equal(LastWholeDay(checkIn, 168), quoteAsIs.GetProperty("partialRefundUntil").GetString());
        Assert.Equal(50m, quoteAsIs.GetProperty("partialRefundPercent").GetDecimal());
        Assert.True(quoteAsIs.GetProperty("selfServiceAvailable").GetBoolean());

        var quoteMoved = (await QuoteAsync(client, moved.Id, checkIn)).GetProperty("cancellation");
        Assert.Equal(LastWholeDay(checkIn, 480), quoteMoved.GetProperty("fullRefundUntil").GetString());
        Assert.Equal(LastWholeDay(checkIn, 168), quoteMoved.GetProperty("partialRefundUntil").GetString());

        var quoteNever = (await QuoteAsync(client, never.Id, checkIn)).GetProperty("cancellation");
        Assert.Equal(JsonValueKind.Null, quoteNever.GetProperty("fullRefundUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, quoteNever.GetProperty("partialRefundUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, quoteNever.GetProperty("partialRefundPercent").ValueKind);
    }

    /// <summary>
    /// The last whole Europe/Rome day, <c>yyyy-MM-dd</c>, that ends at least <paramref name="hours"/> hours before the start of the check-in
    /// day: found by looking at the instants, one day after the other.
    /// </summary>
    private static string LastWholeDay(DateTime checkIn, int hours)
    {
        var day = DateOnly.FromDateTime(checkIn);
        var limit = RomeCalendar.StartOfDayUtc(day).AddHours(-hours);
        var last = day.AddDays(-(hours / 24) - 3);
        for (var candidate = last; candidate <= day; candidate = candidate.AddDays(1))
        {
            if (RomeCalendar.StartOfDayUtc(candidate.AddDays(1)) <= limit)
                last = candidate;
        }

        return last.ToString("yyyy-MM-dd");
    }

    private static async Task<JsonElement> GetPublicHouseAsync(HttpClient client, string orgSlug, string slug)
    {
        var response = await client.GetAsync($"/api/public/orgs/{orgSlug}/properties/{slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> QuoteAsync(HttpClient client, Guid propertyId, DateTime checkIn)
    {
        var response = await client.PostAsJsonAsync("/api/public/bookings/quote", new
        {
            propertyId,
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkIn.AddDays(3).ToString("yyyy-MM-dd"),
            numberOfAdults = 2,
            numberOfChildren = 0,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<OrgEntity> SeedOrgAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "DB-03b Policy Org",
            Slug = $"db03b-policy-{Guid.NewGuid():N}",
            DisplayName = "DB-03b Policy Org",
            ContactEmail = "db03b-policy@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
            StripeConnectedAccountId = "acct_test_connect_ready",
            ConnectChargesEnabled = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private async Task<Property> SeedHouseAsync(OrgEntity org, Action<Property> configure)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "Casa Politica",
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
            Latitude = 45.64m,
            Longitude = 9.13m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 150m,
            CleaningFee = 50m,
            CinCode = "IT108040C2ABCDEFGH",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        configure(property);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }
}
