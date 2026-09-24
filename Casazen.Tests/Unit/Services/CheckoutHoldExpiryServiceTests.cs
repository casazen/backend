using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-21 (A3-13): what the expiry of the public checkout holds does with each booking and with Stripe (EF InMemory; the
/// locking and the concurrent runs are covered on PostgreSQL by <c>CheckoutHoldExpiryPostgresTests</c>).
/// </summary>
public class CheckoutHoldExpiryServiceTests
{
    private const string Account = "acct_bk21_unit";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly Mock<IStripeService> _stripe = new();
    private readonly RecordingEmailQueue _emails = new();
    private readonly Guid _propertyId = Guid.NewGuid();
    private readonly OrgEntity _org;

    public CheckoutHoldExpiryServiceTests()
    {
        _org = new OrgEntity { Name = "Org", Slug = $"org-{Guid.NewGuid():N}", DisplayName = "Org", StripeConnectedAccountId = Account };
        _db.Orgs.Add(_org);
        _db.SaveChanges();

        _stripe
            .Setup(s => s.GetPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, CancellationToken _) => new PaymentIntent { Id = id, Status = "requires_payment_method" });
        _stripe
            .Setup(s => s.CancelPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, string _, CancellationToken _) => new PaymentIntent { Id = id, Status = "canceled" });
        _stripe
            .Setup(s => s.GetSetupIntentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string _, CancellationToken _) => new SetupIntent { Id = id, Status = "requires_payment_method" });
        _stripe
            .Setup(s => s.CancelSetupIntentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string _, string _, CancellationToken _) => new SetupIntent { Id = id, Status = "canceled" });
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_ExpiredPaymentIntentHold_CancelsIntentOnConnectedAccountThenBooking()
    {
        var hold = await SeedHoldAsync(minutesAgo: 20, paymentIntentId: "pi_expired");

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(1, run.Expired);
        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            "pi_expired",
            Account,
            It.Is<string>(key => key.StartsWith($"checkout-hold-expiry:{hold.Id}:pi_expired:", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        var stored = await ReloadAsync(hold.Id);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(BookingCancellationReason.CheckoutHoldExpired, stored.CancellationReason);
        Assert.Equal(PaymentStatus.Canceled, Assert.Single(stored.Payments).Status);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_IntentCreatedOnPreviousAccount_CancelsItOnThatAccount()
    {
        // The org replaced its connected account after the checkout: the intent lives on the account stored on the
        // payment row (BK-02), not on the org's current one.
        var hold = await SeedHoldAsync(minutesAgo: 20, paymentIntentId: "pi_old_account", paymentAccountId: "acct_previous");

        await Service().ExpireDueHoldsAsync();

        _stripe.Verify(s => s.GetPaymentIntentAsync("pi_old_account", "acct_previous", It.IsAny<CancellationToken>()), Times.Once);
        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            "pi_old_account", "acct_previous", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(BookingStatus.Cancelled, (await ReloadAsync(hold.Id)).Status);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_ExpiredSetupIntentHold_CancelsSetupIntentOnConnectedAccount()
    {
        var hold = await SeedHoldAsync(minutesAgo: 20, setupIntentId: "seti_expired");

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(1, run.Expired);
        _stripe.Verify(s => s.CancelSetupIntentAsync(
            "seti_expired", Account, It.Is<string>(key => key.Contains(hold.Id.ToString())), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(BookingCancellationReason.CheckoutHoldExpired, (await ReloadAsync(hold.Id)).CancellationReason);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("processing")]
    [InlineData("requires_capture")]
    public async Task ExpireDueHoldsAsync_GuestAlreadyPaying_LeavesBookingToWebhookAndKeepsDates(string stripeStatus)
    {
        var hold = await SeedHoldAsync(minutesAgo: 20, paymentIntentId: "pi_paid");
        _stripe
            .Setup(s => s.GetPaymentIntentAsync("pi_paid", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_paid", Status = stripeStatus });

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(1, run.LeftToWebhook);
        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var stored = await ReloadAsync(hold.Id);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Null(stored.CancellationReason);
        Assert.Equal(PaymentStatus.Processing, Assert.Single(stored.Payments).Status);

        // Recorded: the next run does not ask Stripe again and the hold keeps its dates (CheckoutHolds.IsExpired).
        var next = await Service().ExpireDueHoldsAsync();
        Assert.Equal(CheckoutHoldExpiryRun.Empty, next);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_GuestPaysDuringCancellation_ReadsAgainAndLeavesBookingToWebhook()
    {
        var hold = await SeedHoldAsync(minutesAgo: 20, paymentIntentId: "pi_race");
        _stripe
            .SetupSequence(s => s.GetPaymentIntentAsync("pi_race", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_race", Status = "requires_action" })
            .ReturnsAsync(new PaymentIntent { Id = "pi_race", Status = "succeeded" });
        _stripe
            .Setup(s => s.CancelPaymentIntentAsync("pi_race", Account, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(
                HttpStatusCode.BadRequest,
                new StripeError { Code = "payment_intent_unexpected_state" },
                "You cannot cancel this PaymentIntent because it has a status of succeeded."));

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(1, run.LeftToWebhook);
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(hold.Id)).Status);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_StripeUnavailable_LeavesHoldUnchangedForNextRun()
    {
        var hold = await SeedHoldAsync(minutesAgo: 20, paymentIntentId: "pi_down");
        _stripe
            .Setup(s => s.GetPaymentIntentAsync("pi_down", Account, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException("Connection reset"));

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(1, run.Failed);
        var stored = await ReloadAsync(hold.Id);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(stored.Payments).Status);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_IntentAlreadyCanceled_ExpiresBookingWithoutCancellingAgain()
    {
        // A previous run cancelled the intent on Stripe but did not save the booking (crash): the retry just finishes.
        var hold = await SeedHoldAsync(minutesAgo: 20, paymentIntentId: "pi_done");
        _stripe
            .Setup(s => s.GetPaymentIntentAsync("pi_done", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_done", Status = "canceled" });

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(1, run.Expired);
        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(BookingStatus.Cancelled, (await ReloadAsync(hold.Id)).Status);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_HoldWithinTtl_IsNotTouched()
    {
        var hold = await SeedHoldAsync(minutesAgo: 5, paymentIntentId: "pi_fresh");

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(CheckoutHoldExpiryRun.Empty, run);
        _stripe.VerifyNoOtherCalls();
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(hold.Id)).Status);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_HostBookingsOnSiteRequestsWithinDeadlineAndPendingWithoutIntent_AreNeverTouched()
    {
        var manual = await SeedHoldAsync(minutesAgo: 600, paymentIntentId: "pi_manual", configure: b =>
        {
            b.Status = BookingStatus.Confirmed;
            b.Source = BookingSource.Manual;
        });
        var confirmedDirect = await SeedHoldAsync(minutesAgo: 600, paymentIntentId: "pi_confirmed",
            configure: b => b.Status = BookingStatus.Confirmed);
        // D5: a "pay at the property" request waits for the host until its own deadline, whatever the checkout TTL (BK-06).
        var onSite = await SeedHoldAsync(minutesAgo: 600, setupIntentId: "seti_onsite", configure: b =>
        {
            b.PaymentOption = PaymentOption.OnSite;
            b.GuestEmailVerifiedAt = DateTime.UtcNow.AddMinutes(-590);
            b.RequestExpiresAt = DateTime.UtcNow.AddHours(1);
        });
        var withoutIntent = await SeedHoldAsync(minutesAgo: 600);

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(CheckoutHoldExpiryRun.Empty, run);
        _stripe.VerifyNoOtherCalls();
        Assert.Equal(BookingStatus.Confirmed, (await ReloadAsync(manual.Id)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await ReloadAsync(confirmedDirect.Id)).Status);
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(onSite.Id)).Status);
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(withoutIntent.Id)).Status);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_OnSiteRequestPastHostDeadline_CancelsItReleasesDatesAndEmailsTheGuest()
    {
        // BK-06: the host did not answer within DirectBooking:OnSiteApprovalHours.
        var request = await SeedOnSiteRequestAsync(emailConfirmed: true, expiresInMinutes: -1);

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(1, run.Expired);
        _stripe.VerifyNoOtherCalls();
        var stored = await ReloadAsync(request.Id);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(BookingCancellationReason.OnSiteRequestExpired, stored.CancellationReason);
        Assert.Equal(PaymentStatus.Canceled, Assert.Single(stored.Payments).Status);
        var email = Assert.Single(_emails.Queued);
        Assert.Equal("onsite-request-expired", email.Template);
        Assert.Equal("guest@example.com", email.To);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_OnSiteRequestEmailNeverConfirmed_CancelsItWithoutEmail()
    {
        // A3-06: an unconfirmed request (possibly a fake address) holds its dates only for the confirmation window and
        // never reaches the host; nobody is emailed when it expires.
        var request = await SeedOnSiteRequestAsync(emailConfirmed: false, expiresInMinutes: -1);

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(1, run.Expired);
        var stored = await ReloadAsync(request.Id);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(BookingCancellationReason.OnSiteEmailNotConfirmed, stored.CancellationReason);
        Assert.Empty(_emails.Queued);
    }

    [Fact]
    public async Task ExpireDueHoldsAsync_OnSiteRequestWithinDeadline_IsNotTouchedWhateverItsAge()
    {
        var request = await SeedOnSiteRequestAsync(emailConfirmed: true, expiresInMinutes: 30, minutesAgo: 600);

        var run = await Service().ExpireDueHoldsAsync();

        Assert.Equal(CheckoutHoldExpiryRun.Empty, run);
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(request.Id)).Status);
        Assert.Empty(_emails.Queued);
    }

    [Fact]
    public async Task ExpireOverlappingHoldsAsync_ExpiredHoldOfOtherDates_IsLeftToTheJob()
    {
        // A2-01: a checkout for 10-12 May has no reason to cancel an abandoned hold of 1-5 May.
        var overlapping = await SeedHoldAsync(minutesAgo: 30, paymentIntentId: "pi_overlap",
            checkIn: new DateTime(2026, 5, 10), checkOut: new DateTime(2026, 5, 14));
        var otherDates = await SeedHoldAsync(minutesAgo: 30, setupIntentId: "seti_other",
            checkIn: new DateTime(2026, 5, 1), checkOut: new DateTime(2026, 5, 5));

        var run = await Service().ExpireOverlappingHoldsAsync(
            _propertyId, new DateTime(2026, 5, 12), new DateTime(2026, 5, 16));

        Assert.Equal(1, run.Expired);
        Assert.Equal(BookingStatus.Cancelled, (await ReloadAsync(overlapping.Id)).Status);
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(otherDates.Id)).Status);
        _stripe.Verify(s => s.GetSetupIntentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpireOverlappingHoldsAsync_SameDayTurnover_DoesNotTouchAdjacentHold()
    {
        var adjacent = await SeedHoldAsync(minutesAgo: 30, paymentIntentId: "pi_adjacent",
            checkIn: new DateTime(2026, 5, 1), checkOut: new DateTime(2026, 5, 5));

        var run = await Service().ExpireOverlappingHoldsAsync(
            _propertyId, new DateTime(2026, 5, 5), new DateTime(2026, 5, 8));

        Assert.Equal(CheckoutHoldExpiryRun.Empty, run);
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(adjacent.Id)).Status);
    }

    private CheckoutHoldExpiryService Service() => new(
        _db,
        _stripe.Object,
        new OnSiteRequestNotifier(_db, _emails, EmailTestHelpers.Links(), NullLogger<OnSiteRequestNotifier>.Instance),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DirectBooking:PendingTtlMinutes"] = "15" })
            .Build(),
        NullLogger<CheckoutHoldExpiryService>.Instance);

    private async Task<Booking> SeedHoldAsync(
        int minutesAgo,
        string? paymentIntentId = null,
        string? setupIntentId = null,
        DateTime? checkIn = null,
        DateTime? checkOut = null,
        Action<Booking>? configure = null,
        string? paymentAccountId = null)
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
        var start = checkIn ?? new DateTime(2026, 11, 1).AddDays(_db.Bookings.Count() * 10);
        var booking = new Booking
        {
            PropertyId = _propertyId,
            OrgId = _org.Id,
            GuestId = Guid.NewGuid(),
            CheckInDate = start,
            CheckOutDate = checkOut ?? start.AddDays(3),
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            StripeSetupIntentId = setupIntentId,
            TotalPrice = 300m,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        configure?.Invoke(booking);
        _db.Bookings.Add(booking);
        if (paymentIntentId is not null)
        {
            _db.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                OrgId = booking.OrgId,
                Amount = booking.TotalPrice,
                Status = PaymentStatus.Pending,
                StripePaymentIntentId = paymentIntentId,
                StripeAccountId = paymentAccountId,
                TransactionId = paymentIntentId,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return booking;
    }

    /// <summary>A "pay at the property" request with its guest, property and cash payment row (read by the notifier).</summary>
    private async Task<Booking> SeedOnSiteRequestAsync(bool emailConfirmed, int expiresInMinutes, int minutesAgo = 20)
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
        if (!await _db.Properties.AnyAsync(p => p.Id == _propertyId))
            _db.Properties.Add(new Property { Id = _propertyId, OrgId = _org.Id, Name = "Villa Rosa", OwnerId = "auth0|host" });
        var guest = new Guest { OrgId = _org.Id, FirstName = "Ada", LastName = "Lovelace", Email = "guest@example.com" };
        _db.Guests.Add(guest);
        var start = new DateTime(2026, 12, 1).AddDays(_db.Bookings.Count() * 10);
        var booking = new Booking
        {
            PropertyId = _propertyId,
            OrgId = _org.Id,
            GuestId = guest.Id,
            CheckInDate = start,
            CheckOutDate = start.AddDays(3),
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.OnSite,
            GuestEmailVerifiedAt = emailConfirmed ? createdAt.AddMinutes(1) : null,
            RequestExpiresAt = DateTime.UtcNow.AddMinutes(expiresInMinutes),
            TotalPrice = 300m,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        _db.Bookings.Add(booking);
        _db.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = Core.Entities.PaymentMethod.CashOnArrival,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return booking;
    }

    private async Task<Booking> ReloadAsync(Guid bookingId)
    {
        _db.ChangeTracker.Clear();
        return await _db.Bookings.AsNoTracking().Include(b => b.Payments).SingleAsync(b => b.Id == bookingId);
    }
}
