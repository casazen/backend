using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>
/// E07/F04/US01/T04 (NF-90, CRM-03, AC-NF90-2): the marketing choices of the guest page over the real pipeline on PostgreSQL, with the guest
/// session of a booking of the site. What the body of the change has to say, and what the guest page does with the address.
/// </summary>
public class GuestMarketingPreferencesHttpTests(ConsentCollectionFactory factory) : IClassFixture<ConsentCollectionFactory>
{
    private sealed record Site(Guid OrgId, string Slug, Guid PropertyId);

    private sealed record Stay(string Code, string Email);

    /// <summary>
    /// Data rule: an opt-out on the guest page is final, so a body that does not say what it wants is refused and changes nothing. A channel that
    /// is named without <c>enabled</c> (the member forgotten, or the object empty) is a 400 and is never taken for a withdrawal: no contact is
    /// made and nothing reaches the suppression list. A body that says <c>enabled</c> goes through.
    /// </summary>
    [PostgresFact]
    public async Task GuestMarketing_AChannelWithoutEnabled_IsRefused_AndNeverTakenForAWithdrawal_Test()
    {
        var site = await SeedSiteAsync();
        var stay = await SeedStayAsync(site, $"ospite.{Guid.NewGuid():N}@example.com");
        var token = await OpenSessionAsync(site, stay);
        var version = ConsentCollectionFactory.MarketingVersion;

        var bodies = new object[]
        {
            new { email = new { address = "altro@example.com" }, consentVersion = version },
            new { email = new { }, consentVersion = version },
            new { whatsapp = new { phone = "+393331112222" }, consentVersion = version },
            new { whatsapp = new { }, consentVersion = version },
        };
        foreach (var body in bodies)
        {
            var refused = await PutAsync(stay.Code, token, body);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("validation_error", problem.GetProperty("code").GetString());
            Assert.True(
                problem.GetProperty("errors").EnumerateObject().Any(error => error.Name.EndsWith("Enabled", StringComparison.OrdinalIgnoreCase)),
                "the 400 names the member that is missing");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.GuestContacts.IgnoreQueryFilters().AnyAsync(c => c.OrgId == site.OrgId));
            Assert.False(await db.SuppressionListEntries.IgnoreQueryFilters().AnyAsync(e => e.OrgId == site.OrgId));
            Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.OrgId == site.OrgId));
        }

        var turnedOn = await PutAsync(stay.Code, token, new { email = new { enabled = true }, consentVersion = version });

        Assert.Equal(HttpStatusCode.OK, turnedOn.StatusCode);
        Assert.Equal("pending", (await ReadAsync(turnedOn)).GetProperty("email").GetProperty("state").GetString());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PutAsync(string code, string bearerToken, object body)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/public/guest/bookings/{code}/marketing")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<string> OpenSessionAsync(Site site, Stay stay)
    {
        using var client = factory.CreateClient();
        var opened = await client.PostAsJsonAsync(
            "/api/public/guest/session", new { orgSlug = site.Slug, bookingCode = stay.Code, email = stay.Email });

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        return (await ReadAsync(opened)).GetProperty("accessToken").GetString()!;
    }

    private async Task<Site> SeedSiteAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Villa Preferenze",
            Slug = $"pref-{Guid.NewGuid():N}",
            DisplayName = "Villa Preferenze",
            ContactEmail = $"host.{Guid.NewGuid():N}@example.com",
            PlanTier = PlanTierEnum.Starter,
            IsActive = true,
        };
        var property = new Property
        {
            OwnerId = $"auth0|pref-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "Casa Preferenze",
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Guest page preferences",
            Address = $"Via Preferenze {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 50m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
        };
        db.AddRange(org, property);
        await db.SaveChangesAsync();
        return new Site(org.Id, org.Slug, property.Id);
    }

    /// <summary>A confirmed booking of the public checkout whose guest has this address and a phone number.</summary>
    private async Task<Stay> SeedStayAsync(Site site, string email)
    {
        var checkIn = TimeProvider.System.TodayInRome().AddDays(20);
        var guest = new Guest
        {
            OrgId = site.OrgId,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = email,
            PhoneNumber = "+393331112222",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var booking = new Booking
        {
            PropertyId = site.PropertyId,
            OrgId = site.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 400m,
            CleaningFee = 50m,
            TouristTax = 20m,
            TouristTaxAmount = 20m,
            TotalPrice = 420m,
        };

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AddRange(guest, booking);
        await db.SaveChangesAsync();
        return new Stay(BookingCodes.Format(booking.BookingCode), email);
    }
}
