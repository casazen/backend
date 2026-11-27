using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The edit of a confirmed booking of the booking site and its money (E08/F02/US01/T01, NF-75) on real PostgreSQL, where the transaction of the edit exists:
/// when Stripe refuses a refund the whole unit of work is rolled back, the row of the refund Stripe refused included. With two payments the refund of the
/// first one is already on Stripe when the second is refused: the database keeps nothing of it, and the retry (the same keys, because the booking is as it
/// was) gets from Stripe the refund it made and ends the edit.
/// </summary>
public class HostBookingEditPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private readonly Mock<IStripeService> _stripe = new();
    private readonly Mock<IPaymentRefundRetryScheduler> _retries = new();
    private readonly Mock<IEmailQueue> _emails = new();

    public HostBookingEditPostgresTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        _emails.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>())).Returns(true);
    }

    /// <summary>
    /// PROOF TEST: money rule, on PostgreSQL. A booking paid 400 is edited to a price of 300 and Stripe refuses the refund: 422
    /// <c>booking_settlement_failed</c>, and a fresh read of the database finds the booking as it was and no refund row at all (not even the one marked
    /// Failed, which was written inside the transaction the refusal rolled back), and the payment untouched.
    /// </summary>
    [PostgresFact]
    public async Task HostBookingEdit_StripeRefuses_RollsTheWholeUnitOfWorkBack_Test()
    {
        var seeded = await SeedAsync(("pi_edit_one", 400m, 3));
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refused());
        var before = await LoadBookingAsync(seeded.BookingId);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => Service(db, total: 300m).UpdateAsync(Edit(seeded, guests: 1), CancellationToken.None));
            Assert.Equal(BookingErrorCodes.SettlementFailed, refused.Code);
        }

        var after = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal((before.TotalPrice, before.NumberOfGuests, before.UpdatedAt), (after.TotalPrice, after.NumberOfGuests, after.UpdatedAt));
        Assert.Empty(await RefundsOfAsync(seeded));
        var payment = Assert.Single(await PaymentsOfAsync(seeded));
        Assert.Equal((PaymentStatus.Completed, 0m), (payment.Status, payment.RefundedAmount));
        _stripe.Verify(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Paid in two payments (60 the newest, 340): the price drops to 300, so 60 go back on the newest and 40 on the other. Stripe makes the first refund and
    /// refuses the second: 422 and the database has nothing of it (the booking as it was, no refund row). The refund of the first payment is on Stripe
    /// already; the retry sends the same keys (the booking did not change), Stripe answers the refund it made, takes the second, and the edit is saved with
    /// both refunds, exactly 100 in all.
    /// </summary>
    [PostgresFact]
    public async Task HostBookingEdit_TheSecondRefundRefused_LeavesNothingInTheDatabase_AndTheRetryEndsIt()
    {
        var seeded = await SeedAsync(("pi_edit_new", 60m, 1), ("pi_edit_old", 340m, 4));
        var made = new Dictionary<string, Refund>(StringComparer.Ordinal);
        var refuseTheOldOne = true;
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .Returns((StripeRefundCreateRequest request, CancellationToken _) =>
            {
                // Stripe answers the same refund to the same key.
                if (made.TryGetValue(request.IdempotencyKey, out var same))
                    return Task.FromResult(same);

                if (refuseTheOldOne && request.PaymentIntentId == "pi_edit_old")
                    return Task.FromException<Refund>(Refused());

                var refund = new Refund
                {
                    Id = $"re_edit_{made.Count}",
                    PaymentIntentId = request.PaymentIntentId,
                    Amount = request.AmountCents,
                    Status = "succeeded",
                    Metadata = new Dictionary<string, string>(request.Metadata),
                };
                made[request.IdempotencyKey] = refund;
                return Task.FromResult(refund);
            });
        var before = await LoadBookingAsync(seeded.BookingId);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => Service(db, total: 300m).UpdateAsync(Edit(seeded, guests: 1), CancellationToken.None));
            Assert.Equal(BookingErrorCodes.SettlementFailed, refused.Code);
        }

        // Stripe made the first refund; the database knows nothing of it and the booking is as it was.
        Assert.Single(made);
        var unchanged = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal((before.TotalPrice, before.NumberOfGuests, before.UpdatedAt), (unchanged.TotalPrice, unchanged.NumberOfGuests, unchanged.UpdatedAt));
        Assert.Empty(await RefundsOfAsync(seeded));

        // The retry: the same request, the same keys; Stripe takes the second payment now.
        refuseTheOldOne = false;
        HostBookingUpdateResult result;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            result = await Service(db, total: 300m).UpdateAsync(Edit(seeded, guests: 1), CancellationToken.None);
        }

        Assert.Equal((BookingSettlementAction.Refund, 100m), (result.Settlement.Action, result.Settlement.Amount));
        Assert.Equal(2, made.Count);
        var done = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal((300m, 1), (done.TotalPrice, done.NumberOfGuests));
        var refunds = await RefundsOfAsync(seeded);
        Assert.Equal(2, refunds.Count);
        Assert.All(refunds, r => Assert.Equal(PaymentRefundStatus.Succeeded, r.Status));
        Assert.Equal(100m, refunds.Sum(r => r.Amount));
        var payments = await PaymentsOfAsync(seeded);
        Assert.Equal(60m, payments.Single(p => p.StripePaymentIntentId == "pi_edit_new").RefundedAmount);
        Assert.Equal(40m, payments.Single(p => p.StripePaymentIntentId == "pi_edit_old").RefundedAmount);
    }

    private static StripeException Refused() =>
        new(HttpStatusCode.BadRequest, new StripeError { Code = "charge_disputed" }, "refused");

    private static DateTime NextYear(int month, int day) =>
        new(TimeProvider.System.TodayInRome().Year + 1, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static HostBookingUpdate Edit(SeededEdit seeded, int guests) =>
        new(seeded.BookingId, seeded.CheckIn, seeded.CheckOut, guests, 0, null, "Arriviamo tardi", null, "auth0|edit-host", CanMoveMoney: true);

    /// <summary>The service over the real database of the scope, the price worked out as <paramref name="total"/> whatever the stay.</summary>
    private HostBookingService Service(AppDbContext db, decimal total)
    {
        var pricing = new Mock<IBookingService>();
        pricing
            .Setup(p => p.PriceHostStayAsync(
                It.IsAny<Property>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Property property, DateTime checkIn, DateTime checkOut, int _, int _, IReadOnlyList<int>? _, CancellationToken _) =>
            {
                var nights = (checkOut - checkIn).Days;
                return new DirectBookingQuote(
                    property.Id,
                    checkIn,
                    checkOut,
                    nights,
                    total / nights,
                    0m,
                    total,
                    new TouristTaxQuote(TouristTaxQuoteStatus.Calculated, 0m, nights, nights, false, [], []),
                    total,
                    "EUR",
                    checkIn.AddDays(-7),
                    new DirectBookingPaymentOptions(DeferredPaymentAvailable: false, DeferredChargeDate: null, FreeCancellationUntil: null),
                    new StayLodging(nights, 0, total / nights, total / nights, total));
            });

        return new HostBookingService(
            db,
            pricing.Object,
            new BookingRepository(db),
            Mock.Of<ICheckoutHoldExpiryService>(),
            ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), new ConfigurationBuilder().Build()),
            Mock.Of<IOnSiteBookingRequestService>(),
            NullLogger<HostBookingService>.Instance,
            TimeProvider.System,
            domainEvents: null,
            channelPolicy: null,
            refundService: new PaymentRefundService(db, _stripe.Object, _retries.Object, _emails.Object, NullLogger<PaymentRefundService>.Instance));
    }

    private sealed record SeededEdit(Guid BookingId, IReadOnlyList<Guid> PaymentIds, DateTime CheckIn, DateTime CheckOut);

    /// <summary>A confirmed booking of the booking site, 2 guests, paid online with the payments given (PaymentIntent, amount, days ago), in an org with its Connect account.</summary>
    private async Task<SeededEdit> SeedAsync(params (string IntentId, decimal Amount, int DaysAgo)[] payments)
    {
        var account = $"acct_edit_{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync($"auth0|edit-host-{Guid.NewGuid():N}");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == property.OrgId);
        org.StripeConnectedAccountId = account;
        org.ConnectChargesEnabled = true;

        var now = DateTime.UtcNow;
        var checkIn = NextYear(10, 1);
        var checkOut = NextYear(10, 4);
        var total = payments.Sum(p => p.Amount);
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
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = total,
            TotalPrice = total,
            FreeRefundDeadline = checkIn.AddDays(-7),
            CreatedAt = now.AddDays(-5),
            UpdatedAt = now.AddDays(-5),
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);

        var paymentIds = new List<Guid>();
        foreach (var (intentId, amount, daysAgo) in payments)
        {
            var payment = new Payment
            {
                BookingId = booking.Id,
                OrgId = property.OrgId,
                Amount = amount,
                Status = PaymentStatus.Completed,
                Method = Casazen.Core.Entities.PaymentMethod.CreditCard,
                TransactionId = intentId,
                StripePaymentIntentId = intentId,
                StripeAccountId = account,
                Description = "Direct checkout - immediate payment",
                ProcessedAt = now.AddDays(-daysAgo),
                CreatedAt = now.AddDays(-daysAgo),
                UpdatedAt = now.AddDays(-daysAgo),
            };
            paymentIds.Add(payment.Id);
            db.Payments.Add(payment);
        }

        await db.SaveChangesAsync();
        return new SeededEdit(booking.Id, paymentIds, checkIn, checkOut);
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
    }

    private async Task<List<Payment>> PaymentsOfAsync(SeededEdit seeded)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Payments.AsNoTracking().Where(p => seeded.PaymentIds.Contains(p.Id)).ToListAsync();
    }

    private async Task<List<PaymentRefund>> RefundsOfAsync(SeededEdit seeded)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PaymentRefunds.AsNoTracking().Where(r => seeded.PaymentIds.Contains(r.PaymentId)).ToListAsync();
    }
}
