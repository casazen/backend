using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
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
/// DB-03 on real PostgreSQL: the marketing consent of the checkout is written in the same transaction as the booking and the
/// guest snapshot, under the lock of the property. A checkout that does not become a booking leaves no consent behind (it
/// holds the IP of the guest), two checkouts of the same dates at once leave the consent of the one that won only, and the
/// register the host reads in the GDPR tab (CO-15) shows the consent with its version.
/// </summary>
public class DirectCheckoutMarketingConsentPostgresTests : IClassFixture<MarketingConsentConfiguredFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private readonly MarketingConsentConfiguredFactory _factory;

    public DirectCheckoutMarketingConsentPostgresTests(MarketingConsentConfiguredFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task TwoCheckoutsOfTheSameDatesAtOnce_BothWithTheBox_LeaveTheConsentOfTheWinnerOnly()
    {
        var (property, _) = await SeedConnectReadyPropertyAsync();
        var checkIn = TimeProvider.System.TodayInRome().AddDays(40);
        var emails = new[] { UniqueEmail(), UniqueEmail() };
        using var first = _factory.CreateClient();
        using var second = _factory.CreateClient();

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/public/bookings", Payload(property.Id, emails[0], checkIn)),
            second.PostAsJsonAsync("/api/public/bookings", Payload(property.Id, emails[1], checkIn)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var records = await db.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrgId == property.OrgId).ToListAsync();
        var record = Assert.Single(records);
        var winner = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == record.GuestId);
        Assert.Contains(winner.Email, emails);
        Assert.True(winner.MarketingConsent);
        Assert.Equal(1, await db.Guests.IgnoreQueryFilters().CountAsync(g => g.OrgId == property.OrgId));
    }

    [PostgresFact]
    public async Task StripeRefusesToStartThePayment_TheAttemptIsRemovedWithItsGuestAndItsConsent()
    {
        var (property, account) = await SeedConnectReadyPropertyAsync();
        FakeStripeService.FailIntentsOfAccount(account);
        var email = UniqueEmail();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/public/bookings", Payload(property.Id, email, TimeProvider.System.TodayInRome().AddDays(30)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("payment_provider_error", problem.RootElement.GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.Email == email));
        Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.OrgId == property.OrgId));
        Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == property.Id));
    }

    [PostgresFact]
    public async Task DiscardCheckoutAttempt_RemovesTheConsentRecordsOfTheGuestWithIt()
    {
        var (property, _) = await SeedConnectReadyPropertyAsync();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = UniqueEmail(),
            Country = "IT",
            MarketingConsent = true,
            MarketingConsentDate = DateTime.UtcNow,
            ConsentVersion = ConsentVersion,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(20),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(23),
            NumberOfAdults = 2,
            NumberOfGuests = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 500m,
            TotalPrice = 500m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        using (var seed = _factory.Services.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.Guests.Add(guest);
            seedDb.Bookings.Add(booking);
            seedDb.GuestConsentRecords.Add(new GuestConsentRecord
            {
                OrgId = property.OrgId,
                GuestId = guest.Id,
                Purpose = GuestConsentPurpose.Marketing,
                Action = GuestConsentAction.Granted,
                Version = MarketingConsentConfiguredFactory.Version,
                Source = GuestConsentSource.BookingCheckout,
                IpAddress = "203.0.113.7",
                RecordedAt = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBookingRepository>().DiscardCheckoutAttemptAsync(booking.Id);

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.Id == guest.Id));
        Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.GuestId == guest.Id));
    }

    [PostgresFact]
    public async Task ConsentGivenAtTheCheckout_IsShownByTheGdprTabAndCanBeWithdrawnByTheHostOnTheGuestsRequest()
    {
        var (property, _) = await SeedConnectReadyPropertyAsync();
        var email = UniqueEmail();
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/public/bookings", Payload(property.Id, email, TimeProvider.System.TodayInRome().AddDays(45)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guestId = await db.Guests.IgnoreQueryFilters().Where(g => g.Email == email).Select(g => g.Id).SingleAsync();

        var granted = await gdpr.GetGuestPrivacySummaryAsync(property.OrgId, guestId);
        Assert.True(granted.Marketing.Granted);
        Assert.Equal(MarketingConsentConfiguredFactory.Version, granted.Marketing.Version);
        var event1 = Assert.Single(granted.ConsentHistory, h => h.Purpose == GuestConsentPurpose.Marketing);
        Assert.Equal((GuestConsentAction.Granted, GuestConsentSource.BookingCheckout), (event1.Action, event1.Source));

        await gdpr.UpdateMarketingConsentAsync(property.OrgId, guestId, false, "Email dell'ospite del 02/10", "auth0|host");

        var withdrawn = await gdpr.GetGuestPrivacySummaryAsync(property.OrgId, guestId);
        Assert.False(withdrawn.Marketing.Granted);
        var history = withdrawn.ConsentHistory.Where(h => h.Purpose == GuestConsentPurpose.Marketing).ToList();
        Assert.Equal(2, history.Count);
        // The withdrawal keeps the version of the text the guest had agreed to.
        Assert.Equal(MarketingConsentConfiguredFactory.Version, history.Single(h => h.Action == GuestConsentAction.Withdrawn).Version);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static string UniqueEmail() => $"db03.{Guid.NewGuid():N}@example.com";

    private static object Payload(Guid propertyId, string email, DateTime checkIn) => new
    {
        propertyId,
        checkInDate = checkIn.ToString("yyyy-MM-dd"),
        checkOutDate = checkIn.AddDays(3).ToString("yyyy-MM-dd"),
        numberOfAdults = 2,
        numberOfChildren = 0,
        guest = new
        {
            firstName = "Mario",
            lastName = "Rossi",
            email,
            phone = "+393331234567",
            country = "IT",
        },
        consent = new { dataProcessing = true, consentVersion = ConsentVersion },
        paymentOption = "Immediate",
        marketingConsent = true,
    };

    private async Task<(Property Property, string Account)> SeedConnectReadyPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = $"acct_db03_{Guid.NewGuid():N}";

        var org = new OrgEntity
        {
            Name = "DB-03 Org",
            Slug = $"db03-{Guid.NewGuid():N}",
            DisplayName = "DB-03 Org",
            ContactEmail = "db03@example.com",
            PlanTier = PlanTierEnum.Starter,
            IsActive = true,
            StripeConnectedAccountId = account,
            ConnectChargesEnabled = true,
        };
        db.Orgs.Add(org);

        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "DB-03 Villa",
            Slug = $"villa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via DB03 {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
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
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (property, account);
    }
}
