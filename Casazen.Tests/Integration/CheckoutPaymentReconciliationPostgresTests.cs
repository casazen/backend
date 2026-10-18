using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// A checkout hold the expiry job left "to the payment webhook" (payment <c>Processing</c>) and that the webhook never settled
/// must not stay pending for ever: Stripe is asked for the real state of the PaymentIntent. Paid: settled like the webhook.
/// Never paid: back to pending, and the expiry job cancels the booking and releases the dates in the same run.
/// </summary>
public class CheckoutPaymentReconciliationPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const decimal HoldAmount = 350m;

    private readonly CasazenWebApplicationFactory _factory;
    private readonly Mock<IStripeService> _stripe = new();
    private string _status = "requires_payment_method";
    private bool _intentMissing;
    private string? _kind = "direct-booking";

    public CheckoutPaymentReconciliationPostgresTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        _stripe
            .Setup(s => s.GetPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, CancellationToken _) =>
            {
                if (_intentMissing)
                    throw new StripeException { StripeError = new StripeError { Code = "resource_missing" } };

                return new PaymentIntent
                {
                    Id = id,
                    Status = _status,
                    Metadata = _kind is null ? [] : new Dictionary<string, string> { ["kind"] = _kind },
                };
            });
        _stripe
            .Setup(s => s.CancelPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, string _, CancellationToken _) => new PaymentIntent { Id = id, Status = "canceled" });
    }

    [PostgresFact]
    public async Task Reconcile_InFlightPaymentThatSucceededOnStripe_ConfirmsTheBooking()
    {
        var hold = await SeedStuckHoldAsync(paymentMinutesAgo: 30);
        _status = "succeeded";

        await RunJobAsync();

        var booking = await LoadBookingAsync(hold.BookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(PaymentStatus.Completed, Assert.Single(booking.Payments).Status);
    }

    [PostgresFact]
    public async Task Reconcile_InFlightPaymentNeverPaid_IsCancelledAndTheDatesAreReleasedByTheSameRun()
    {
        var hold = await SeedStuckHoldAsync(paymentMinutesAgo: 30);
        _status = "requires_payment_method";

        await RunJobAsync();

        var booking = await LoadBookingAsync(hold.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(BookingCancellationReason.CheckoutHoldExpired, booking.CancellationReason);
        Assert.Equal(PaymentStatus.Canceled, Assert.Single(booking.Payments).Status);
    }

    [PostgresFact]
    public async Task Reconcile_PaymentIntentUnknownToStripe_IsCancelled()
    {
        var hold = await SeedStuckHoldAsync(paymentMinutesAgo: 30);
        _intentMissing = true;

        await RunJobAsync();

        var booking = await LoadBookingAsync(hold.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
    }

    [PostgresFact]
    public async Task Reconcile_PaymentStillProcessingOnStripe_IsLeftAlone()
    {
        var hold = await SeedStuckHoldAsync(paymentMinutesAgo: 30);
        _status = "processing";

        await RunJobAsync();

        var booking = await LoadBookingAsync(hold.BookingId);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Equal(PaymentStatus.Processing, Assert.Single(booking.Payments).Status);
    }

    [PostgresFact]
    public async Task Reconcile_PaymentSeenInFlightJustNow_IsNotReadFromStripeYet()
    {
        var hold = await SeedStuckHoldAsync(paymentMinutesAgo: 2);
        _status = "succeeded";

        await RunJobAsync();

        _stripe.Verify(s => s.GetPaymentIntentAsync(hold.PaymentIntentId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(BookingStatus.Pending, (await LoadBookingAsync(hold.BookingId)).Status);
    }

    [PostgresFact]
    public async Task Reconcile_SucceededIntentThatIsNotACheckoutPayment_IsLeftAlone()
    {
        var hold = await SeedStuckHoldAsync(paymentMinutesAgo: 30);
        _status = "succeeded";
        _kind = null;

        await RunJobAsync();

        Assert.Equal(BookingStatus.Pending, (await LoadBookingAsync(hold.BookingId)).Status);
    }

    /// <summary>Reconciliation and then expiry, as the recurring job runs them. The tests share a database: each one checks only its own hold.</summary>
    private async Task RunJobAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var reconciliation = new CheckoutPaymentReconciliationService(
            db,
            _stripe.Object,
            TestCheckoutPaymentSettlement.Create(db, stripe: _stripe.Object, configuration: services.GetRequiredService<IConfiguration>()),
            NullLogger<CheckoutPaymentReconciliationService>.Instance);
        var expiry = new CheckoutHoldExpiryService(
            db,
            _stripe.Object,
            services.GetRequiredService<OnSiteRequestNotifier>(),
            services.GetRequiredService<IConfiguration>(),
            NullLogger<CheckoutHoldExpiryService>.Instance);

        await reconciliation.ReconcileInFlightPaymentsAsync();
        await expiry.ExpireDueHoldsAsync();
    }

    private sealed record SeededHold(Guid BookingId, string PaymentIntentId);

    /// <summary>A hold older than the TTL whose payment the expiry job saw in flight (<c>Processing</c>) <paramref name="paymentMinutesAgo"/> ago.</summary>
    private async Task<SeededHold> SeedStuckHoldAsync(int paymentMinutesAgo)
    {
        var account = $"acct_recon_{Guid.NewGuid():N}";
        var seeded = await _factory.SeedPropertyAsync($"auth0|recon-host-{Guid.NewGuid():N}");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == seeded.OrgId);
        org.StripeConnectedAccountId = account;
        org.ConnectChargesEnabled = true;
        var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;

        var createdAt = DateTime.UtcNow.AddHours(-2);
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var checkIn = new DateTime(DateTime.UtcNow.Year + 1, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            TotalPrice = HoldAmount,
            FreeRefundDeadline = checkIn.AddDays(-7),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var paymentIntentId = $"pi_recon_{Guid.NewGuid():N}";
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = property.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Processing,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            StripeAccountId = account,
            Description = "Direct checkout - immediate payment",
            CreatedAt = createdAt,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-paymentMinutesAgo),
        });
        await db.SaveChangesAsync();
        return new SeededHold(booking.Id, paymentIntentId);
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().Include(b => b.Payments).SingleAsync(b => b.Id == bookingId);
    }
}
