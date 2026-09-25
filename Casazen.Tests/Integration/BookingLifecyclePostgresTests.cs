using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-07 (A2-07, A2-08, A2-30) on real PostgreSQL, through the whole pipeline (auth, TN-3, ProblemDetails): the host
/// changes a booking with a DTO that cannot touch status or prices, confirms the pending bookings entered by hand,
/// cancels a paid booking with its refund on Stripe and an email to the guest, and checks out stays of any length.
/// </summary>
public class BookingLifecyclePostgresTests : IClassFixture<BookingLifecyclePostgresTests.LifecycleFactory>
{
    private const string HostRole = "PropertyOwner";
    private const string Account = "acct_it_lifecycle";
    private readonly LifecycleFactory _factory;

    public BookingLifecyclePostgresTests(LifecycleFactory factory) => _factory = factory;

    private FakeStripeService Stripe => (FakeStripeService)_factory.Services.GetRequiredService<IStripeService>();

    [PostgresFact]
    public async Task Update_BodyWithStatusPricesAndGuest_IgnoresThemAndRepricesTheManualBooking()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var checkIn = NextYear(10, 1);
        var created = await CreateManualBookingAsync(host, property.Id, checkIn, checkIn.AddDays(4));
        var before = await LoadAsync(created);

        var response = await host.PutAsJsonAsync($"/api/bookings/{created}", new
        {
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkIn.AddDays(5).ToString("yyyy-MM-dd"),
            numberOfGuests = 3,
            numberOfChildren = 0,
            specialRequests = "Culla in camera",
            // Mass-assignment attempt (A2-07): none of these may reach the booking.
            status = "Cancelled",
            source = "Airbnb",
            basePrice = 1m,
            totalPrice = 1m,
            touristTax = 999m,
            cleaningFee = 0m,
            guestId = Guid.NewGuid(),
            orgId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        var stored = await LoadAsync(created);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(BookingSource.Manual, stored.Source);
        Assert.Equal(before.GuestId, stored.GuestId);
        Assert.Equal(before.OrgId, stored.OrgId);
        // Priced again by the server: 5 nights x 100 + 50 cleaning; no tourist tax rate for Rome in CasaZen.
        Assert.Equal(550m, stored.BasePrice);
        Assert.Equal(50m, stored.CleaningFee);
        Assert.Equal(550m, stored.TotalPrice);
        Assert.Equal(0m, stored.TouristTax);
        Assert.Equal(3, stored.NumberOfGuests);
        Assert.Equal(checkIn.AddDays(5), stored.CheckOutDate);
        Assert.Equal("Culla in camera", stored.SpecialRequests);
    }

    [PostgresFact]
    public async Task Update_DatesOverlappingAnotherBooking_Returns409AndKeepsTheDates()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var october1 = NextYear(10, 1);
        await CreateManualBookingAsync(host, property.Id, october1, october1.AddDays(4));
        var second = await CreateManualBookingAsync(host, property.Id, october1.AddDays(9), october1.AddDays(13));

        var response = await host.PutAsJsonAsync($"/api/bookings/{second}", new
        {
            checkInDate = october1.AddDays(2).ToString("yyyy-MM-dd"),
            checkOutDate = october1.AddDays(6).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("booking_dates_unavailable", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        var stored = await LoadAsync(second);
        Assert.Equal(october1.AddDays(9), stored.CheckInDate);
        Assert.Equal(october1.AddDays(13), stored.CheckOutDate);
        Assert.Equal(450m, stored.TotalPrice);
    }

    [PostgresFact]
    public async Task Update_DatesOfABookingFromTheBookingSite_Returns422AndNotesStillChange()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Confirmed, BookingSource.Direct, NextYear(11, 2), NextYear(11, 5));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var moved = await host.PutAsJsonAsync($"/api/bookings/{bookingId}", new
        {
            checkInDate = NextYear(11, 3).ToString("yyyy-MM-dd"),
            checkOutDate = NextYear(11, 6).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
        });
        var notes = await host.PutAsJsonAsync($"/api/bookings/{bookingId}", new
        {
            checkInDate = NextYear(11, 2).ToString("yyyy-MM-dd"),
            checkOutDate = NextYear(11, 5).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            specialRequests = "Arrivo alle 22",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, moved.StatusCode);
        Assert.Equal("booking_update_source_locked", (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
        var stored = await LoadAsync(bookingId);
        Assert.Equal(NextYear(11, 2), stored.CheckInDate);
        Assert.Equal(300m, stored.TotalPrice);
        Assert.Equal("Arrivo alle 22", stored.SpecialRequests);
    }

    [PostgresFact]
    public async Task Update_StayStartedCancelledOrCheckInMovedToThePast_Returns422WithStableCodes()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var today = TimeProvider.System.TodayInRome();
        var checkedIn = await SeedBookingAsync(property, BookingStatus.CheckedIn, BookingSource.Manual, today.AddDays(-1), today.AddDays(2));
        var cancelled = await SeedBookingAsync(property, BookingStatus.Cancelled, BookingSource.Manual, today.AddDays(10), today.AddDays(12));
        var future = await SeedBookingAsync(property, BookingStatus.Confirmed, BookingSource.Manual, today.AddDays(20), today.AddDays(22));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var extendStay = await PutStayAsync(host, checkedIn, today.AddDays(-1), today.AddDays(3));
        var editCancelled = await PutStayAsync(host, cancelled, today.AddDays(10), today.AddDays(12));
        var moveToPast = await PutStayAsync(host, future, today.AddDays(-2), today.AddDays(1));
        var notesDuringStay = await PutStayAsync(host, checkedIn, today.AddDays(-1), today.AddDays(2), "Chiave al vicino");

        Assert.Equal("booking_stay_locked", await ProblemCodeAsync(extendStay, HttpStatusCode.UnprocessableEntity));
        Assert.Equal("booking_not_editable", await ProblemCodeAsync(editCancelled, HttpStatusCode.UnprocessableEntity));
        Assert.Equal("booking_checkin_in_past", await ProblemCodeAsync(moveToPast, HttpStatusCode.UnprocessableEntity));
        // The dates of a stay already started are never validated again: the notes can still change (A2-08).
        Assert.Equal(HttpStatusCode.OK, notesDuringStay.StatusCode);
        Assert.Equal("Chiave al vicino", (await LoadAsync(checkedIn)).SpecialRequests);
        Assert.Equal(today.AddDays(20), (await LoadAsync(future)).CheckInDate);
    }

    [PostgresFact]
    public async Task BackfillCleaningFeeSql_BookingsPricedByCasaZen_GetThePropertyFeeCappedAtTheBasePrice()
    {
        var (_, property) = await SeedHostPropertyAsync();
        var direct = await SeedBookingAsync(property, BookingStatus.Confirmed, BookingSource.Direct, NextYear(3, 1), NextYear(3, 4));
        var manual = await SeedBookingAsync(property, BookingStatus.Confirmed, BookingSource.Manual, NextYear(3, 10), NextYear(3, 12));
        var channel = await SeedBookingAsync(property, BookingStatus.Confirmed, BookingSource.Airbnb, NextYear(3, 20), NextYear(3, 22));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == manual).ExecuteUpdateAsync(u => u.SetProperty(b => b.BasePrice, 30m));
            await db.Database.ExecuteSqlRawAsync(
                Casazen.Infrastructure.Migrations.AddBookingCleaningFeeAndCancellationNote.BackfillCleaningFeeSql);
        }

        Assert.Equal(50m, (await LoadAsync(direct)).CleaningFee);
        Assert.Equal(30m, (await LoadAsync(manual)).CleaningFee);
        Assert.Equal(0m, (await LoadAsync(channel)).CleaningFee);
    }

    [PostgresFact]
    public async Task Confirm_PendingManualBookingLeftByTheOldCode_ConfirmsItOnce()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Pending, BookingSource.Manual, NextYear(9, 10), NextYear(9, 13));
        var checkoutHold = await SeedBookingAsync(property, BookingStatus.Pending, BookingSource.Direct, NextYear(9, 20), NextYear(9, 22));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var confirm = await host.PostAsync($"/api/bookings/{bookingId}/approve", null);
        var again = await host.PostAsync($"/api/bookings/{bookingId}/approve", null);
        var hold = await host.PostAsync($"/api/bookings/{checkoutHold}/approve", null);

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal("Confirmed", (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var stored = await LoadAsync(bookingId);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("booking_not_pending", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hold.StatusCode);
        Assert.Equal("booking_not_confirmable", (await hold.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(BookingStatus.Pending, (await LoadAsync(checkoutHold)).Status);
    }

    [PostgresFact]
    public async Task Cancel_PaidBooking_RefundsOnStripeKeepsTheReasonAndEmailsTheGuest()
    {
        var (hostId, property) = await SeedHostPropertyAsync(connectAccount: Account);
        var seed = await SeedPaidBookingAsync(property, 400m);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.PostAsJsonAsync(
            $"/api/bookings/{seed.BookingId}/cancel", new { refundAmount = 400m, reason = "Guasto alla caldaia" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refund = Assert.Single(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);
        Assert.Equal(Account, refund.ConnectedAccountId);
        Assert.Equal(40_000, refund.AmountCents);
        var stored = await LoadAsync(seed.BookingId);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal("Guasto alla caldaia", stored.CancellationNote);
        var email = Assert.Single(_factory.Emails.Queued, e =>
            e.Template == EmailTemplates.Names.GuestBookingCancelled && e.To == seed.GuestEmail);
        Assert.Contains("400,00 €", email.Content.HtmlBody);
        Assert.DoesNotContain("caldaia", email.Content.HtmlBody);
        var detail = await host.GetFromJsonAsync<JsonElement>($"/api/bookings/{seed.BookingId}");
        Assert.Equal("Guasto alla caldaia", detail.GetProperty("cancellationNote").GetString());
    }

    [PostgresFact]
    public async Task CheckOut_ThreeNightStayOnItsDepartureDay_ChecksOut()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var today = TimeProvider.System.TodayInRome();
        var stay = await SeedBookingAsync(property, BookingStatus.CheckedIn, BookingSource.Manual, today.AddDays(-3), today);
        var early = await SeedBookingAsync(property, BookingStatus.CheckedIn, BookingSource.Manual, today.AddDays(5), today.AddDays(8));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.PostAsync($"/api/bookings/{stay}/check-out", null);
        var tooEarly = await host.PostAsync($"/api/bookings/{early}/check-out", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CheckedOut", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(BookingStatus.CheckedOut, (await LoadAsync(stay)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooEarly.StatusCode);
        Assert.Equal("booking_checkout_too_early", (await tooEarly.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(BookingStatus.CheckedIn, (await LoadAsync(early)).Status);
    }

    [PostgresFact]
    public async Task LifecycleActions_BookingOfAnotherOrg_Answer404AndChangeNothing()
    {
        var (_, property) = await SeedHostPropertyAsync();
        var pending = await SeedBookingAsync(property, BookingStatus.Pending, BookingSource.Manual, NextYear(8, 1), NextYear(8, 4));
        var (otherHostId, otherProperty) = await SeedHostPropertyAsync();
        using var intruder = _factory.CreateAuthenticatedClient(otherHostId, HostRole);

        var update = await intruder.PutAsJsonAsync($"/api/bookings/{pending}", new
        {
            checkInDate = NextYear(8, 1).ToString("yyyy-MM-dd"),
            checkOutDate = NextYear(8, 4).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            specialRequests = "hijacked",
        });
        var confirm = await intruder.PostAsync($"/api/bookings/{pending}/approve", null);
        var checkOut = await intruder.PostAsync($"/api/bookings/{pending}/check-out", null);
        var cancel = await intruder.PostAsync($"/api/bookings/{pending}/cancel", null);
        var quote = await intruder.PostAsJsonAsync("/api/bookings/quote", new
        {
            propertyId = property.Id,
            checkInDate = NextYear(8, 1).ToString("yyyy-MM-dd"),
            checkOutDate = NextYear(8, 4).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
        });

        Assert.All(new[] { update, confirm, checkOut, cancel, quote }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var stored = await LoadAsync(pending);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.NotEqual("hijacked", stored.SpecialRequests);
        Assert.NotEqual(property.OrgId, otherProperty.OrgId);
    }

    [PostgresFact]
    public async Task CreateManualBooking_MinorsWithAges_QuoteAsksTheAgesAndTheTaxExemptsTheChild()
    {
        // Firenze (rates migrated by RS-7): 6,00 per person per night, under-12s exempt.
        var (hostId, property) = await SeedHostPropertyAsync(city: "Firenze");
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var checkIn = TimeProvider.System.TodayInRome().AddDays(60);
        var stay = new
        {
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkIn.AddDays(3).ToString("yyyy-MM-dd"),
        };

        var withoutAges = await host.PostAsJsonAsync("/api/bookings/quote", new
        {
            propertyId = property.Id,
            stay.checkInDate,
            stay.checkOutDate,
            numberOfGuests = 3,
            numberOfChildren = 1,
        });
        var createWithoutAges = await host.PostAsJsonAsync("/api/bookings", ManualPayload(property.Id, stay.checkInDate, stay.checkOutDate, null));
        var created = await host.PostAsJsonAsync("/api/bookings", ManualPayload(property.Id, stay.checkInDate, stay.checkOutDate, [8]));

        Assert.Equal(HttpStatusCode.OK, withoutAges.StatusCode);
        var tax = (await withoutAges.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("touristTax");
        Assert.Equal("ChildAgesRequired", tax.GetProperty("status").GetString());
        Assert.True(tax.GetProperty("ageRulesApply").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, createWithoutAges.StatusCode);
        Assert.Equal(
            "tourist_tax_child_ages_required",
            (await createWithoutAges.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var stored = await LoadAsync((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        // 2 adults x 6,00 x 3 nights; the 8-year-old is exempt.
        Assert.Equal(36.00m, stored.TouristTax);
        Assert.Equal(350m + 36.00m, stored.TotalPrice);
        Assert.Equal(2, stored.NumberOfAdults);
        Assert.Equal(1, stored.NumberOfChildren);
    }

    private static Task<HttpResponseMessage> PutStayAsync(
        HttpClient host,
        Guid bookingId,
        DateTime checkIn,
        DateTime checkOut,
        string? notes = null) =>
        host.PutAsJsonAsync($"/api/bookings/{bookingId}", new
        {
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkOut.ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            specialRequests = notes,
        });

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
    }

    private static DateTime NextYear(int month, int day) =>
        new(TimeProvider.System.TodayInRome().Year + 1, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static object ManualPayload(Guid propertyId, string checkIn, string checkOut, int[]? childrenAges) => new
    {
        propertyId,
        checkInDate = checkIn,
        checkOutDate = checkOut,
        numberOfGuests = 3,
        numberOfChildren = 1,
        childrenAges,
        guest = new
        {
            firstName = "Mario",
            lastName = "Rossi",
            email = $"mario.{Guid.NewGuid():N}@example.com",
            phone = "+393331234567",
            country = "Italia",
        },
    };

    private static async Task<Guid> CreateManualBookingAsync(HttpClient host, Guid propertyId, DateTime checkIn, DateTime checkOut)
    {
        var response = await host.PostAsJsonAsync("/api/bookings", new
        {
            propertyId,
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkOut.ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            guest = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "Italia",
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>A host with its org and an active property (100 per night, 50 cleaning, 4 guests).</summary>
    private async Task<(string HostId, Property Property)> SeedHostPropertyAsync(string? city = null, string? connectAccount = null)
    {
        var hostId = $"auth0|pc07-{Guid.NewGuid():N}";
        var seeded = await _factory.SeedPropertyAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == seeded.Id);
        if (city is not null)
            property.City = city;
        if (connectAccount is not null)
        {
            var org = await db.Orgs.SingleAsync(o => o.Id == seeded.OrgId);
            org.StripeConnectedAccountId = connectAccount;
            org.ConnectChargesEnabled = true;
        }

        await db.SaveChangesAsync();
        return (hostId, property);
    }

    private async Task<Guid> SeedBookingAsync(
        Property property,
        BookingStatus status,
        BookingSource source,
        DateTime checkIn,
        DateTime checkOut)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = status,
            Source = source,
            BasePrice = 300m,
            TotalPrice = 300m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AddRange(guest, booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private sealed record PaidSeed(Guid BookingId, string PaymentIntentId, string GuestEmail);

    /// <summary>A confirmed booking of the booking site, paid on Stripe on the host's connected account.</summary>
    private async Task<PaidSeed> SeedPaidBookingAsync(Property property, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = $"giulia.{Guid.NewGuid():N}@example.com",
        };
        var today = TimeProvider.System.TodayInRome();
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = today.AddDays(30),
            CheckOutDate = today.AddDays(33),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = amount,
            TotalPrice = amount,
            FreeRefundDeadline = today.AddDays(23),
        };
        var paymentIntentId = $"pi_pc07_{Guid.NewGuid():N}";
        db.AddRange(guest, booking, new Payment
        {
            BookingId = booking.Id,
            OrgId = property.OrgId,
            Amount = amount,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            StripeAccountId = Account,
        });
        await db.SaveChangesAsync();
        return new PaidSeed(booking.Id, paymentIntentId, guest.Email);
    }

    private async Task<Booking> LoadAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == bookingId);
    }

    /// <summary>The default integration factory with a recording email queue.</summary>
    public sealed class LifecycleFactory : CasazenWebApplicationFactory
    {
        internal RecordingEmailQueue Emails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IEmailQueue>(services);
                services.AddSingleton<IEmailQueue>(Emails);
            });
        }
    }
}
