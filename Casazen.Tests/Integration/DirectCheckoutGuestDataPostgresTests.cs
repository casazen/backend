using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-18 (A3-33) on real PostgreSQL: a public checkout that does not become a booking leaves no guest with personal data
/// and consent behind. The guest snapshot is written with its booking in one insert, after the stay is validated and the
/// dates are free; when Stripe refuses to start the payment the attempt is removed with its guest. A guest that anything
/// else still references is never removed.
/// </summary>
public class DirectCheckoutGuestDataPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private readonly CasazenWebApplicationFactory _factory;

    public DirectCheckoutGuestDataPostgresTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task CreateDirectBooking_PastCheckIn_Returns422AndStoresNoGuest()
    {
        var (property, _) = await SeedConnectReadyPropertyAsync();
        var email = UniqueEmail();
        var yesterday = RomeToday().AddDays(-1);
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/public/bookings", Payload(property.Id, email, yesterday, yesterday.AddDays(3), "Immediate"));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "direct_booking_invalid_stay");
        Assert.False(await GuestExistsAsync(email));
        Assert.Equal(0, await BookingCountAsync(property.Id));
    }

    [PostgresFact]
    public async Task CreateDirectBooking_StripeRefusesThePaymentIntent_Returns503AndLeavesNoBookingNorGuest()
    {
        var (property, account) = await SeedConnectReadyPropertyAsync();
        FakeStripeService.FailIntentsOfAccount(account);
        var email = UniqueEmail();
        var checkIn = RomeToday().AddDays(30);
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/public/bookings", Payload(property.Id, email, checkIn, checkIn.AddDays(4), "Immediate"));

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "payment_provider_error");
        Assert.False(await GuestExistsAsync(email));
        Assert.Equal(0, await BookingCountAsync(property.Id));
    }

    [PostgresFact]
    public async Task CreateDirectBooking_StripeRefusesTheSetupIntent_Returns503AndLeavesNoBookingNorGuest()
    {
        var (property, account) = await SeedConnectReadyPropertyAsync();
        FakeStripeService.FailIntentsOfAccount(account);
        var email = UniqueEmail();
        var checkIn = RomeToday().AddDays(30);
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/public/bookings",
            Payload(property.Id, email, checkIn, checkIn.AddDays(4), "OnCancellationDeadline"));

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "payment_provider_error");
        Assert.False(await GuestExistsAsync(email));
        Assert.Equal(0, await BookingCountAsync(property.Id));
    }

    [PostgresFact]
    public async Task CreateDirectBooking_TwoCheckoutsOfTheSameDatesAtOnce_StoresOneBookingAndOnlyItsGuest()
    {
        var (property, _) = await SeedConnectReadyPropertyAsync();
        var checkIn = RomeToday().AddDays(40);
        var emails = new[] { UniqueEmail(), UniqueEmail() };
        using var first = _factory.CreateClient();
        using var second = _factory.CreateClient();

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/public/bookings", Payload(property.Id, emails[0], checkIn, checkIn.AddDays(3), "Immediate")),
            second.PostAsJsonAsync("/api/public/bookings", Payload(property.Id, emails[1], checkIn, checkIn.AddDays(3), "Immediate")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var stored = await GuestsOfPropertyBookingsAsync(property.Id);
        var guest = Assert.Single(stored);
        var rejectedEmail = emails.Single(e => e != guest.Email);
        Assert.False(await GuestExistsAsync(rejectedEmail));
    }

    [PostgresFact]
    public async Task DiscardCheckoutAttemptAsync_FailedCheckout_RemovesBookingPaymentsAndGuestInOneGo()
    {
        var (property, _) = await SeedConnectReadyPropertyAsync();
        var guest = NewGuest(property.OrgId);
        var booking = NewPendingBooking(property, guest.Id, RomeToday().AddDays(20));
        await SeedAsync(guest, booking, new Payment
        {
            BookingId = booking.Id,
            OrgId = property.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = PaymentMethod.CashOnArrival,
            Description = "Direct checkout - payment on site",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBookingRepository>().DiscardCheckoutAttemptAsync(booking.Id);
        }

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.Id == booking.Id));
        Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(p => p.BookingId == booking.Id));
        Assert.False(await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.Id == guest.Id));
    }

    [PostgresFact]
    public async Task DiscardCheckoutAttemptAsync_GuestWithAnotherBooking_KeepsTheGuestAndTheOtherBooking()
    {
        var (property, _) = await SeedConnectReadyPropertyAsync();
        var guest = NewGuest(property.OrgId);
        var failed = NewPendingBooking(property, guest.Id, RomeToday().AddDays(20));
        var other = NewPendingBooking(property, guest.Id, RomeToday().AddDays(60));
        other.Status = BookingStatus.Confirmed;
        await SeedAsync(guest, failed, other);

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBookingRepository>().DiscardCheckoutAttemptAsync(failed.Id);
        }

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.Id == failed.Id));
        Assert.True(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.Id == other.Id));
        var kept = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == guest.Id);
        Assert.Equal(guest.Email, kept.Email);
    }

    private static DateTime RomeToday() => TimeProvider.System.TodayInRome();

    private static string UniqueEmail() => $"bk18.{Guid.NewGuid():N}@example.com";

    private static object Payload(Guid propertyId, string email, DateTime checkIn, DateTime checkOut, string paymentOption) => new
    {
        propertyId,
        checkInDate = checkIn.ToString("yyyy-MM-dd"),
        checkOutDate = checkOut.ToString("yyyy-MM-dd"),
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
        paymentOption,
    };

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<bool> GuestExistsAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.Email == email);
    }

    private async Task<int> BookingCountAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.IgnoreQueryFilters().CountAsync(b => b.PropertyId == propertyId);
    }

    private async Task<List<Guest>> GuestsOfPropertyBookingsAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.PropertyId == propertyId)
            .Select(b => b.Guest)
            .ToListAsync();
    }

    private static Guest NewGuest(Guid orgId) => new()
    {
        OrgId = orgId,
        FirstName = "Mario",
        LastName = "Rossi",
        Email = UniqueEmail(),
        Country = "IT",
        DataProcessingConsentDate = DateTime.UtcNow,
        ConsentVersion = ConsentVersion,
        ConsentDate = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Booking NewPendingBooking(Property property, Guid guestId, DateTime checkIn) => new()
    {
        PropertyId = property.Id,
        OrgId = property.OrgId,
        GuestId = guestId,
        CheckInDate = checkIn,
        CheckOutDate = checkIn.AddDays(3),
        NumberOfAdults = 2,
        NumberOfGuests = 2,
        Status = BookingStatus.Pending,
        Source = BookingSource.Direct,
        PaymentOption = PaymentOption.OnSite,
        BasePrice = 500m,
        TotalPrice = 500m,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task SeedAsync(Guest guest, params object[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Guests.Add(guest);
        foreach (var row in rows)
            db.Add(row);
        await db.SaveChangesAsync();
    }

    private async Task<(Property Property, string Account)> SeedConnectReadyPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = $"acct_bk18_{Guid.NewGuid():N}";

        var org = new OrgEntity
        {
            Name = "BK-18 Org",
            Slug = $"bk18-{Guid.NewGuid():N}",
            DisplayName = "BK-18 Org",
            ContactEmail = "bk18@example.com",
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
            Name = "BK-18 Villa",
            Description = "Integration test property",
            Address = $"Via BK18 {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 150m,
            CleaningFee = 50m,
            DamageDeposit = 200m,
            CinCode = "IT058091C27G5FFZDZ",
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
