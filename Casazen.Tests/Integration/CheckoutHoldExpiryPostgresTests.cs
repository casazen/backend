using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-21 (A3-13) on real PostgreSQL: the <c>checkout-hold-expiry</c> job cancels the intent of each abandoned checkout
/// hold on the host's connected account, then the booking; it leaves alone the holds whose guest has paid, the holds
/// still within the TTL, host bookings and confirmed bookings; two concurrent runs cancel once. Public availability, the
/// host calendar and the iCal export free the dates of an expired hold before the job has run.
/// </summary>
/// <remarks>
/// The job runs over the whole test database: the Stripe mock answers for any intent (not paid, cancellable) and each
/// test checks only the intents it seeded.
/// </remarks>
public class CheckoutHoldExpiryPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private const string HostRole = "PropertyOwner";

    private readonly CasazenWebApplicationFactory _factory;
    private readonly ConcurrentDictionary<string, string> _intentStatuses = new();
    private readonly ConcurrentDictionary<string, int> _cancellations = new();
    private readonly Mock<IStripeService> _stripe = new();

    public CheckoutHoldExpiryPostgresTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        _stripe
            .Setup(s => s.GetPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, CancellationToken _) => new PaymentIntent { Id = id, Status = StatusOf(id) });
        _stripe
            .Setup(s => s.CancelPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, string _, CancellationToken _) => Cancel(id));
    }

    [PostgresFact]
    public async Task ExecuteAsync_ExpiredHoldWithPaymentIntent_CancelsIntentOnConnectedAccountAndExpiresBooking()
    {
        var (_, property, account) = await SeedCheckoutReadyPropertyAsync();
        var paymentIntentId = NewPaymentIntentId();
        var hold = await SeedHoldAsync(property, NextYear(10, 1), NextYear(10, 5), minutesAgo: 30, paymentIntentId);

        await RunJobAsync();

        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            paymentIntentId,
            account,
            It.Is<string>(key => key.StartsWith($"checkout-hold-expiry:{hold}:{paymentIntentId}:", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        var stored = await LoadBookingAsync(hold);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(BookingCancellationReason.CheckoutHoldExpired, stored.CancellationReason);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(stored.Payments).Status);
    }

    [PostgresFact]
    public async Task ExecuteAsync_PaymentIntentAlreadySucceeded_LeavesBookingUntouchedForWebhook()
    {
        var (_, property, account) = await SeedCheckoutReadyPropertyAsync();
        var paymentIntentId = NewPaymentIntentId();
        _intentStatuses[paymentIntentId] = "succeeded";
        var hold = await SeedHoldAsync(property, NextYear(10, 1), NextYear(10, 5), minutesAgo: 30, paymentIntentId);
        var before = await LoadBookingAsync(hold);

        await RunJobAsync();

        _stripe.Verify(s => s.GetPaymentIntentAsync(paymentIntentId, account, It.IsAny<CancellationToken>()), Times.Once);
        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            paymentIntentId, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var stored = await LoadBookingAsync(hold);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Null(stored.CancellationReason);
        Assert.Equal(before.UpdatedAt, stored.UpdatedAt);
        // Stripe's answer is recorded on the payment: the dates stay taken until the webhook confirms the booking.
        Assert.Equal(PaymentStatus.Processing, Assert.Single(stored.Payments).Status);
        Assert.Contains(NextYear(10, 2).ToString("yyyy-MM-dd"), await BookedDatesAsync(property.Id, NextYear(10, 1)));

        await RunJobAsync();
        _stripe.Verify(s => s.GetPaymentIntentAsync(paymentIntentId, account, It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task ExecuteAsync_HoldWithinTtl_IsNotTouched()
    {
        var (_, property, _) = await SeedCheckoutReadyPropertyAsync();
        var paymentIntentId = NewPaymentIntentId();
        var hold = await SeedHoldAsync(property, NextYear(10, 1), NextYear(10, 5), minutesAgo: 5, paymentIntentId);

        await RunJobAsync();

        _stripe.Verify(s => s.GetPaymentIntentAsync(paymentIntentId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        var stored = await LoadBookingAsync(hold);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(stored.Payments).Status);
    }

    [PostgresFact]
    public async Task ExecuteAsync_ManualAndConfirmedBookings_AreNeverTouched()
    {
        var (_, property, _) = await SeedCheckoutReadyPropertyAsync();
        var manualIntent = NewPaymentIntentId();
        var confirmedIntent = NewPaymentIntentId();
        var manual = await SeedHoldAsync(property, NextYear(10, 1), NextYear(10, 5), minutesAgo: 600, manualIntent, b =>
        {
            b.Status = BookingStatus.Confirmed;
            b.Source = BookingSource.Manual;
        });
        var confirmed = await SeedHoldAsync(property, NextYear(10, 10), NextYear(10, 12), minutesAgo: 600, confirmedIntent,
            b => b.Status = BookingStatus.Confirmed);
        // D5: a "pay at the property" request waits for the host's approval, never the checkout TTL (BK-06).
        var onSite = await SeedHoldAsync(property, NextYear(10, 20), NextYear(10, 22), minutesAgo: 600, paymentIntentId: null,
            b => b.PaymentOption = PaymentOption.OnSite);

        await RunJobAsync();

        _stripe.Verify(s => s.GetPaymentIntentAsync(manualIntent, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _stripe.Verify(s => s.GetPaymentIntentAsync(confirmedIntent, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(BookingStatus.Confirmed, (await LoadBookingAsync(manual)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await LoadBookingAsync(confirmed)).Status);
        Assert.Equal(BookingStatus.Pending, (await LoadBookingAsync(onSite)).Status);
    }

    [PostgresFact]
    public async Task AvailabilityCalendarAndICalExport_ExpiredHoldBeforeJob_DatesAreFree()
    {
        var (hostId, property, _) = await SeedCheckoutReadyPropertyAsync();
        var expired = await SeedHoldAsync(property, NextYear(10, 1), NextYear(10, 5), minutesAgo: 30, NewPaymentIntentId());
        var fresh = await SeedHoldAsync(property, NextYear(10, 10), NextYear(10, 12), minutesAgo: 5, NewPaymentIntentId());
        var exportToken = await SeedExportFeedAsync(property);

        var bookedDates = await BookedDatesAsync(property.Id, NextYear(10, 1));
        for (var night = NextYear(10, 1); night < NextYear(10, 5); night = night.AddDays(1))
            Assert.DoesNotContain(night.ToString("yyyy-MM-dd"), bookedDates);
        Assert.Contains(NextYear(10, 10).ToString("yyyy-MM-dd"), bookedDates);

        using var anonymous = _factory.CreateClient();
        var ics = await anonymous.GetStringAsync($"/api/public/ical/{exportToken}");
        Assert.DoesNotContain($"UID:booking-{expired}", ics);
        Assert.Contains($"UID:booking-{fresh}", ics);

        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var calendar = await host.GetFromJsonAsync<JsonElement>(
            $"/api/bookings/calendar?propertyId={property.Id}" +
            $"&startDate={NextYear(9, 25):yyyy-MM-dd}&endDate={NextYear(10, 31):yyyy-MM-dd}&timezone=UTC");
        var calendarIds = calendar.GetProperty("bookings").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(expired, calendarIds);
        Assert.Contains(fresh, calendarIds);

        // Reads only leave the hold out: cancelling it (with its intent) is the job's work.
        Assert.Equal(BookingStatus.Pending, (await LoadBookingAsync(expired)).Status);
    }

    [PostgresFact]
    public async Task ExecuteAsync_TwoConcurrentRuns_CancelIntentOnce()
    {
        var (_, property, account) = await SeedCheckoutReadyPropertyAsync();
        var paymentIntentId = NewPaymentIntentId();
        var hold = await SeedHoldAsync(property, NextYear(10, 1), NextYear(10, 5), minutesAgo: 30, paymentIntentId);

        // The run that locks the hold first waits inside Stripe until the other run has finished: that run must skip
        // the locked hold instead of cancelling it a second time.
        var otherRunFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stripe
            .Setup(s => s.GetPaymentIntentAsync(paymentIntentId, account, It.IsAny<CancellationToken>()))
            .Returns(async (string id, string? _, CancellationToken _) =>
            {
                await Task.WhenAny(otherRunFinished.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                return new PaymentIntent { Id = id, Status = StatusOf(id) };
            });

        var first = RunJobAsync();
        var second = RunJobAsync();
        _ = Task.WhenAny(first, second).ContinueWith(_ => otherRunFinished.TrySetResult(), TaskScheduler.Default);
        await Task.WhenAll(first, second);

        Assert.True(otherRunFinished.Task.IsCompleted);
        Assert.Equal(1, _cancellations.GetValueOrDefault(paymentIntentId));
        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            paymentIntentId, account, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var stored = await LoadBookingAsync(hold);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(BookingCancellationReason.CheckoutHoldExpired, stored.CancellationReason);
    }

    [PostgresFact]
    public async Task PublicCheckout_ExpiredHoldWhoseGuestHasPaid_KeepsDatesAndReturns409()
    {
        // The cleanup before a new booking runs the same routine as the job: the late payment wins, no double booking.
        var (_, property, _) = await SeedCheckoutReadyPropertyAsync();
        var paymentIntentId = NewPaymentIntentId();
        FakeStripeService.SetIntentStatus(paymentIntentId, "succeeded");
        var hold = await SeedHoldAsync(property, NextYear(10, 1), NextYear(10, 5), minutesAgo: 30, paymentIntentId);
        using var guest = _factory.CreateClient();

        var response = await guest.PostAsJsonAsync(
            "/api/public/bookings", CheckoutPayload(property.Id, NextYear(10, 2), NextYear(10, 4)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await LoadBookingAsync(hold);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Equal(PaymentStatus.Processing, Assert.Single(stored.Payments).Status);
        Assert.DoesNotContain(FakeStripeService.CancelledIntents, c => c.IntentId == paymentIntentId);
    }

    private static string NewPaymentIntentId() => $"pi_bk21_{Guid.NewGuid():N}";

    private string StatusOf(string intentId) =>
        _intentStatuses.TryGetValue(intentId, out var status) ? status : "requires_payment_method";

    private PaymentIntent Cancel(string intentId)
    {
        _cancellations.AddOrUpdate(intentId, 1, (_, count) => count + 1);
        _intentStatuses[intentId] = "canceled";
        return new PaymentIntent { Id = intentId, Status = "canceled" };
    }

    /// <summary>One run of the recurring job, in its own DI scope (own DbContext), as Hangfire runs it.</summary>
    private async Task RunJobAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var expiry = new CheckoutHoldExpiryService(
            services.GetRequiredService<AppDbContext>(),
            _stripe.Object,
            services.GetRequiredService<IConfiguration>(),
            NullLogger<CheckoutHoldExpiryService>.Instance);
        await new CheckoutHoldExpiryJob(expiry, NullLogger<CheckoutHoldExpiryJob>.Instance)
            .ExecuteAsync(CancellationToken.None);
    }

    private static DateTime NextYear(int month, int day) =>
        new(TimeProvider.System.TodayInRome().Year + 1, month, day, 0, 0, 0, DateTimeKind.Utc);

    private async Task<List<string?>> BookedDatesAsync(Guid propertyId, DateTime from)
    {
        using var anonymous = _factory.CreateClient();
        var availability = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/public/bookings/property/{propertyId}/availability" +
            $"?startDate={from:yyyy-MM-dd}&endDate={from.AddDays(40):yyyy-MM-dd}");
        return availability.GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()).ToList();
    }

    private static object CheckoutPayload(Guid propertyId, DateTime checkIn, DateTime checkOut) => new
    {
        propertyId,
        checkInDate = checkIn.ToString("yyyy-MM-dd"),
        checkOutDate = checkOut.ToString("yyyy-MM-dd"),
        numberOfAdults = 2,
        numberOfChildren = 0,
        guest = new
        {
            firstName = "Giulia",
            lastName = "Bianchi",
            email = $"giulia.{Guid.NewGuid():N}@example.com",
            phone = "+393339876543",
            country = "IT",
        },
        consent = new { dataProcessing = true, consentVersion = ConsentVersion },
        paymentOption = nameof(PaymentOption.Immediate),
    };

    /// <summary>A host whose org has its own connected account (Connect ready) and an active, bookable property.</summary>
    private async Task<(string HostId, Property Property, string Account)> SeedCheckoutReadyPropertyAsync()
    {
        var hostId = $"auth0|bk21-host-{Guid.NewGuid():N}";
        var account = $"acct_bk21_{Guid.NewGuid():N}";
        var seeded = await _factory.SeedPropertyAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == seeded.OrgId);
        org.StripeConnectedAccountId = account;
        org.ConnectChargesEnabled = true;
        var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();
        return (hostId, property, account);
    }

    /// <summary>A public checkout hold: Pending direct booking created <paramref name="minutesAgo"/> minutes ago.</summary>
    private async Task<Guid> SeedHoldAsync(
        Property property,
        DateTime checkIn,
        DateTime checkOut,
        int minutesAgo,
        string? paymentIntentId,
        Action<Booking>? configure = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var createdAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            TotalPrice = 350m,
            FreeRefundDeadline = checkIn.AddDays(-7),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        configure?.Invoke(booking);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        if (paymentIntentId is not null)
        {
            db.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                OrgId = property.OrgId,
                Amount = booking.TotalPrice,
                Status = PaymentStatus.Pending,
                StripePaymentIntentId = paymentIntentId,
                TransactionId = paymentIntentId,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
        }

        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<Guid> SeedExportFeedAsync(Property property)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feed = new PropertyICalFeed
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            ExportToken = Guid.NewGuid(),
        };
        db.PropertyICalFeeds.Add(feed);
        await db.SaveChangesAsync();
        return feed.ExportToken;
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().Include(b => b.Payments).SingleAsync(b => b.Id == bookingId);
    }
}
