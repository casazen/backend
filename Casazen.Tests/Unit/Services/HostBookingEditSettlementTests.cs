using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The edit of a confirmed booking of the booking site and the money that follows it (E08/F02/US01/T01, NF-75): a lower price refunds the exact
/// difference once, a higher one charges the saved card or sends a payment link, the same price moves nothing, what was paid offline is reported and
/// not refunded online, and when Stripe refuses the booking is not changed.
/// </summary>
public class HostBookingEditSettlementTests
{
    private const string Account = "acct_host";
    private static readonly Guid OrgId = Guid.Parse("7c1f0a52-3b7e-4c11-9d2a-5e8b7c6d4a21");

    /// <summary>2026-11-06 09:00 in Rome: the stays below are in December.</summary>
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));

    // --- The rules, pure -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_SameTotalAsPaid_MovesNothing()
    {
        var plan = BookingEditSettlementRules.Plan(300m, 300m, [new BookingRefundableShare(Guid.NewGuid(), 300m)], collectsOnline: true, hasSavedCard: true);

        Assert.Equal(BookingSettlementAction.None, plan.Action);
        Assert.Equal(0m, plan.Amount);
        Assert.Equal(0m, plan.OfflineRefundDue);
        Assert.Empty(plan.Refunds);
    }

    [Fact]
    public void Plan_LowerPrice_RefundsNewestPaymentFirstNeverAboveWhatItCanReturn_AndReportsTheRest()
    {
        var newest = Guid.NewGuid();
        var oldest = Guid.NewGuid();

        // Paid 500 in two payments (the newest 120, of which 100 can still go back; the oldest 380). The new price is 300: 200 are due back.
        var plan = BookingEditSettlementRules.Plan(
            300m,
            500m,
            [new BookingRefundableShare(newest, 100m), new BookingRefundableShare(oldest, 380m)],
            collectsOnline: true,
            hasSavedCard: false);

        Assert.Equal(BookingSettlementAction.Refund, plan.Action);
        Assert.Equal(200m, plan.Amount);
        Assert.Equal(0m, plan.OfflineRefundDue);
        Assert.Equal([new BookingRefundPart(newest, 100m), new BookingRefundPart(oldest, 100m)], plan.Refunds);

        // What the Stripe payments can no longer return is for the host to give back: 200 due, only 150 refundable online.
        var partly = BookingEditSettlementRules.Plan(300m, 500m, [new BookingRefundableShare(oldest, 150m)], collectsOnline: true, hasSavedCard: false);
        Assert.Equal(BookingSettlementAction.Refund, partly.Action);
        Assert.Equal(150m, partly.Amount);
        Assert.Equal(50m, partly.OfflineRefundDue);
    }

    [Fact]
    public void Plan_PaidOffline_IsReportedAndNeverRefundedOnline()
    {
        var plan = BookingEditSettlementRules.Plan(300m, 400m, [], collectsOnline: false, hasSavedCard: false);

        Assert.Equal(BookingSettlementAction.None, plan.Action);
        Assert.Equal(0m, plan.Amount);
        Assert.Equal(100m, plan.OfflineRefundDue);
        Assert.Empty(plan.Refunds);
    }

    [Theory]
    [InlineData(true, true, BookingSettlementAction.ChargeSavedCard)]
    [InlineData(true, false, BookingSettlementAction.PaymentLink)]
    [InlineData(false, true, BookingSettlementAction.None)]
    [InlineData(false, false, BookingSettlementAction.None)]
    public void Plan_HigherPrice_ChargesTheCardOrSendsTheLink_OnlyWhenTheGuestPaysOnline(bool collectsOnline, bool hasSavedCard, BookingSettlementAction expected)
    {
        var plan = BookingEditSettlementRules.Plan(380m, 300m, [], collectsOnline, hasSavedCard);

        Assert.Equal(expected, plan.Action);
        Assert.Equal(expected == BookingSettlementAction.None ? 0m : 80m, plan.Amount);
    }

    [Fact]
    public void Plan_AmountsAreToTheCent()
    {
        // Half away from zero, as every amount of CasaZen: 100.005 is 100.01, and 100.004 is 100.00.
        Assert.Equal(100.01m, BookingEditSettlementRules.Plan(100.005m, 0m, [], collectsOnline: true, hasSavedCard: false).Amount);
        Assert.Equal(100m, BookingEditSettlementRules.Plan(100.004m, 0m, [], collectsOnline: true, hasSavedCard: false).Amount);
    }

    [Fact]
    public void Keys_AreTheSameForTheSameEdit_AndDifferByPaymentAndAmount()
    {
        var booking = Guid.NewGuid();
        var payment = Guid.NewGuid();
        var at = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(BookingEditSettlementRules.RefundKey(booking, at, payment), BookingEditSettlementRules.RefundKey(booking, at, payment));
        Assert.StartsWith($"booking-edit-refund:{booking}:", BookingEditSettlementRules.RefundKey(booking, at, payment), StringComparison.Ordinal);
        Assert.NotEqual(BookingEditSettlementRules.RefundKey(booking, at, payment), BookingEditSettlementRules.RefundKey(booking, at, Guid.NewGuid()));
        Assert.NotEqual(BookingEditSettlementRules.RefundKey(booking, at, payment), BookingEditSettlementRules.RefundKey(booking, at.AddSeconds(1), payment));
        Assert.NotEqual(BookingEditSettlementRules.ChargeKey(booking, at, 80m), BookingEditSettlementRules.ChargeKey(booking, at, 81m));
    }

    // --- The proofs ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: money rule (NF-75, AC-NF75-3). A confirmed booking of the booking site paid 400 on Stripe is edited to a price of 300: exactly the
    /// difference (100) is refunded, on that payment, with the idempotency key of the edit, and the booking change and the refund are saved together.
    /// The same request sent again moves nothing (the price is paid), and the refund of the same key asked twice is the one refund: Stripe is called once,
    /// there is one refund row, and the payment has been given back 100 and no more.
    /// </summary>
    [Fact]
    public async Task HostBookingEdit_PriceDecrease_RefundsExactDifferenceOnce_Test()
    {
        var h = new Harness();
        var (bookingId, paymentId) = await h.SeedAsync(payments: [h.StripePayment("pi_1", 400m)]);
        var before = await h.ReadBookingAsync(bookingId);
        var requests = h.AllowRefunds();
        h.PriceOf(300m);

        await using (var db = h.NewDb())
        {
            var result = await h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 1), CancellationToken.None);

            Assert.Equal(BookingSettlementAction.Refund, result.Settlement.Action);
            Assert.Equal(BookingSettlementState.Done, result.Settlement.State);
            Assert.Equal(100m, result.Settlement.Amount);
            Assert.Equal(0m, result.Settlement.OfflineRefundDue);
            Assert.Equal(300m, result.Booking.TotalPrice);
        }

        // Exactly the difference, once, on the payment that was paid, with the key of this edit.
        var refund = Assert.Single(requests);
        Assert.Equal(10_000, refund.AmountCents);
        Assert.Equal("pi_1", refund.PaymentIntentId);
        Assert.Equal(Account, refund.ConnectedAccountId);
        Assert.Equal(BookingEditSettlementRules.RefundKey(bookingId, before.UpdatedAt, paymentId), refund.IdempotencyKey);

        var stored = await h.ReadBookingAsync(bookingId);
        Assert.Equal(300m, stored.TotalPrice);
        Assert.Equal(1, stored.NumberOfGuests);
        var rows = await h.ReadRefundsAsync();
        var row = Assert.Single(rows);
        Assert.Equal((paymentId, 100m, PaymentRefundStatus.Succeeded), (row.PaymentId, row.Amount, row.Status));
        Assert.Equal(refund.IdempotencyKey, row.IdempotencyKey);
        var payment = await h.ReadPaymentAsync(paymentId);
        Assert.Equal(100m, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);

        // The same request again: the price is paid, nothing moves.
        await using (var db = h.NewDb())
        {
            var again = await h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 1), CancellationToken.None);
            Assert.Equal(BookingSettlementAction.None, again.Settlement.Action);
        }

        // The refund of the same key asked once more (a request retried after an answer was lost): the refund that exists, not a second one.
        await using (var db = h.NewDb())
        {
            var refunds = new PaymentRefundService(db, h.Stripe.Object, Mock.Of<IPaymentRefundRetryScheduler>(), h.Emails.Object, NullLogger<PaymentRefundService>.Instance, Clock);
            var second = await refunds.RefundAsync(new PaymentRefundRequest(paymentId, 100m, "Booking edit", null, refund.IdempotencyKey));
            Assert.Equal(row.Id, second.Id);
        }

        Assert.Single(requests);
        Assert.Single(await h.ReadRefundsAsync());
        Assert.Equal(100m, (await h.ReadPaymentAsync(paymentId)).RefundedAmount);
    }

    /// <summary>
    /// PROOF TEST: money rule (NF-75, AC-NF75-3). Stripe refuses the refund: the answer is 422 <c>booking_settlement_failed</c> and the booking is exactly
    /// as it was (price, guests, notes, last change): the edit and the money are one decision. The refund row Stripe refused is written inside the
    /// transaction the refusal rolls back; it is never a refund that happened.
    /// </summary>
    [Fact]
    public async Task HostBookingEdit_StripeRefuses_SavesNothing_Test()
    {
        var h = new Harness();
        var (bookingId, paymentId) = await h.SeedAsync(payments: [h.StripePayment("pi_1", 400m)]);
        var before = await h.ReadBookingAsync(bookingId);
        h.Stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Stripe.StripeException(System.Net.HttpStatusCode.BadRequest, new Stripe.StripeError { Code = "charge_disputed" }, "refused"));
        h.PriceOf(300m);

        await using (var db = h.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 1, notes: "Nota nuova"), CancellationToken.None));
            Assert.Equal(BookingErrorCodes.SettlementFailed, refused.Code);
        }

        var stored = await h.ReadBookingAsync(bookingId);
        Assert.Equal(before.TotalPrice, stored.TotalPrice);
        Assert.Equal(before.NumberOfGuests, stored.NumberOfGuests);
        Assert.Equal(before.SpecialRequests, stored.SpecialRequests);
        Assert.Equal(before.UpdatedAt, stored.UpdatedAt);
        var payment = await h.ReadPaymentAsync(paymentId);
        Assert.Equal(0m, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.DoesNotContain(await h.ReadRefundsAsync(), r => r.Status is PaymentRefundStatus.Succeeded or PaymentRefundStatus.Pending);
    }

    // --- The other ways the money follows the price ----------------------------------------------------------------------------

    [Fact]
    public async Task HostBookingEdit_PriceIncrease_ChargesTheSavedCardForTheDifference_AndRecordsThePayment()
    {
        var h = new Harness();
        var deferred = h.StripePayment("pi_1", 300m);
        deferred.Description = DeferredCharges.PaymentDescription;
        var (bookingId, _) = await h.SeedAsync(
            payments: [deferred],
            configure: b =>
            {
                b.PaymentOption = PaymentOption.OnCancellationDeadline;
                b.StripeSetupIntentId = "seti_1";
                b.StripeCustomerId = "cus_1";
                b.StripePaymentMethodId = "pm_1";
                b.TotalPrice = 300m;
            });
        var before = await h.ReadBookingAsync(bookingId);
        h.PriceOf(380m);
        h.Stripe
            .Setup(s => s.ChargePaymentMethodAsync(
                Account, "cus_1", "pm_1", 8_000, "eur", It.IsAny<Dictionary<string, string>>(), BookingEditSettlementRules.ChargeKey(bookingId, before.UpdatedAt, 80m), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Stripe.PaymentIntent { Id = "pi_2", Status = "succeeded", Amount = 8_000 });

        await using (var db = h.NewDb())
        {
            var result = await h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 3), CancellationToken.None);

            Assert.Equal(BookingSettlementAction.ChargeSavedCard, result.Settlement.Action);
            Assert.Equal(BookingSettlementState.Done, result.Settlement.State);
            Assert.Equal(80m, result.Settlement.Amount);
        }

        var charge = Assert.Single(await h.ReadPaymentsAsync(), p => p.StripePaymentIntentId == "pi_2");
        Assert.Equal((80m, PaymentStatus.Completed, Account), (charge.Amount, charge.Status, charge.StripeAccountId));
        Assert.Equal(380m, (await h.ReadBookingAsync(bookingId)).TotalPrice);
        h.Links.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HostBookingEdit_PriceIncrease_WithNoSavedCard_SendsAPaymentLinkAfterTheNewTotalIsSaved()
    {
        var h = new Harness();
        var (bookingId, _) = await h.SeedAsync(payments: [h.StripePayment("pi_1", 306m)]);
        h.PriceOf(386m);
        decimal totalWhenTheLinkWasMade = 0m;
        h.Links.Setup(l => l.EnsureAvailableAsync(OrgId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        h.Links
            .Setup(l => l.CreateAsync(bookingId, It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken _) =>
            {
                totalWhenTheLinkWasMade = (await h.ReadBookingAsync(id)).TotalPrice;
                return new BookingPaymentLink(id, "https://example.com/pay", 80m, "EUR", Clock.GetUtcNow().UtcDateTime, true);
            });

        await using (var db = h.NewDb())
        {
            var result = await h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 3), CancellationToken.None);

            Assert.Equal(BookingSettlementAction.PaymentLink, result.Settlement.Action);
            Assert.Equal(80m, result.Settlement.Amount);
        }

        // The link is for the balance of the booking as the edit left it, never of the old one.
        Assert.Equal(386m, totalWhenTheLinkWasMade);
        h.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HostBookingEdit_ACardChargedAtItsDeadlineAndNotYetCharged_MovesNothingNow()
    {
        var h = new Harness();
        var setup = h.StripePayment("seti_1", 300m);
        setup.Status = PaymentStatus.Pending;
        setup.StripePaymentIntentId = null;
        var (bookingId, _) = await h.SeedAsync(
            payments: [setup],
            configure: b =>
            {
                b.PaymentOption = PaymentOption.OnCancellationDeadline;
                b.StripeSetupIntentId = "seti_1";
                b.StripeCustomerId = "cus_1";
                b.StripePaymentMethodId = "pm_1";
                b.TotalPrice = 300m;
            });
        h.PriceOf(380m);

        await using (var db = h.NewDb())
        {
            var result = await h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 3), CancellationToken.None);

            // The deadline charge takes the new total: nothing is charged now, and the card is not touched.
            Assert.Equal(BookingSettlementAction.None, result.Settlement.Action);
            Assert.Equal(380m, result.Booking.TotalPrice);
        }

        h.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HostBookingEdit_PaidOffline_ReportsWhatTheHostGivesBack_AndCallsNoStripe()
    {
        var h = new Harness();
        var cash = new Payment
        {
            OrgId = OrgId,
            Amount = 400m,
            Status = PaymentStatus.Completed,
            Method = Core.Entities.PaymentMethod.CashOnArrival,
            TransactionId = "casazen-cash",
            CreatedAt = Clock.GetUtcNow().UtcDateTime,
            UpdatedAt = Clock.GetUtcNow().UtcDateTime,
        };
        var (bookingId, _) = await h.SeedAsync(payments: [cash]);
        h.PriceOf(300m);

        await using var db = h.NewDb();
        var result = await h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 1), CancellationToken.None);

        Assert.Equal(BookingSettlementAction.None, result.Settlement.Action);
        Assert.Equal(100m, result.Settlement.OfflineRefundDue);
        Assert.Equal(300m, result.Booking.TotalPrice);
        h.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HostBookingEdit_APaymentStripeIsProcessing_Is409AndChangesNothing()
    {
        var h = new Harness();
        var processing = h.StripePayment("pi_9", 100m);
        processing.Status = PaymentStatus.Processing;
        var (bookingId, _) = await h.SeedAsync(payments: [h.StripePayment("pi_1", 400m), processing]);
        var before = await h.ReadBookingAsync(bookingId);
        h.PriceOf(300m);

        await using (var db = h.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(
                () => h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 1), CancellationToken.None));
            Assert.Equal(BookingErrorCodes.EditPaymentInProgress, refused.Code);
        }

        Assert.Equal(before.TotalPrice, (await h.ReadBookingAsync(bookingId)).TotalPrice);
        h.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HostBookingEdit_ARequestOfTheBookingSite_IsLockedAndNotesStillChange()
    {
        var h = new Harness();
        var (bookingId, _) = await h.SeedAsync(configure: b => b.Status = BookingStatus.Pending);
        h.PriceOf(300m);

        await using var db = h.NewDb();
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 1), CancellationToken.None));
        var notes = await h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 2, notes: "Arrivo alle 22"), CancellationToken.None);

        Assert.Equal(BookingErrorCodes.SourceLocked, refused.Code);
        Assert.Equal("Arrivo alle 22", notes.Booking.SpecialRequests);
        Assert.Equal(BookingSettlementAction.None, notes.Settlement.Action);
    }

    [Fact]
    public async Task HostBookingEdit_ABookingEnteredByTheHost_IsPricedAgainButItsMoneyIsSettledByHand()
    {
        var h = new Harness();
        var (bookingId, _) = await h.SeedAsync(payments: [h.StripePayment("pi_1", 400m)], configure: b => b.Source = BookingSource.Manual);
        h.PriceOf(300m);

        await using var db = h.NewDb();
        var result = await h.Service(db).UpdateAsync(h.GuestsEdit(bookingId, guests: 1), CancellationToken.None);

        Assert.Equal(300m, result.Booking.TotalPrice);
        Assert.Equal(BookingSettlementAction.None, result.Settlement.Action);
        h.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EditQuote_SaysWhatTheEditWouldDo_AndDoesNothing()
    {
        var h = new Harness();
        var (bookingId, paymentId) = await h.SeedAsync(payments: [h.StripePayment("pi_1", 400m)]);
        var before = await h.ReadBookingAsync(bookingId);
        h.PriceOf(300m);

        await using var db = h.NewDb();
        var quote = await h.Service(db).QuoteEditAsync(h.GuestsEdit(bookingId, guests: 1), CancellationToken.None);

        Assert.Equal((400m, 300m, 400m, -100m), (quote.PreviousTotal, quote.NewTotal, quote.PaidSoFar, quote.Difference));
        Assert.Equal((BookingSettlementAction.Refund, 100m, 0m, "EUR"), (quote.Action, quote.Amount, quote.OfflineRefundDue, quote.Currency));

        // Nothing was refunded, saved or sent.
        h.Stripe.VerifyNoOtherCalls();
        var stored = await h.ReadBookingAsync(bookingId);
        Assert.Equal((before.TotalPrice, before.NumberOfGuests, before.UpdatedAt), (stored.TotalPrice, stored.NumberOfGuests, stored.UpdatedAt));
        Assert.Empty(await h.ReadRefundsAsync());
        Assert.Equal(0m, (await h.ReadPaymentAsync(paymentId)).RefundedAmount);
    }

    [Fact]
    public async Task EditQuote_ABookingOfAnotherOrg_IsNotFound()
    {
        var h = new Harness();
        var (bookingId, _) = await h.SeedAsync();
        h.PriceOf(300m);

        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(h.Database).Options,
            new OtherOrgTenant());
        await Assert.ThrowsAsync<NotFoundException>(
            () => h.Service(db).QuoteEditAsync(h.GuestsEdit(bookingId, guests: 1), CancellationToken.None));
    }

    // --- The words -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void GuestBookingModified_SaysTheNewStayAndTheMoney_AndNeverTheNotesOfTheHost()
    {
        var summary = new Casazen.Infrastructure.Email.Templates.BookingEmailSummary(
            "ABCD-1234", "Casa <del Lago>", new DateTime(2026, 12, 10), new DateTime(2026, 12, 13), 2, 300m, 0m, 0m, 300m, "EUR");

        var italian = Casazen.Infrastructure.Email.Templates.EmailTemplates.GuestBookingModified(
            Casazen.Infrastructure.Email.Templates.EmailTemplates.DefaultCulture,
            "Giulia",
            summary,
            refundedAmount: 100m,
            chargedAmount: 0m,
            paymentLinkFollows: false,
            Casazen.Infrastructure.Email.Templates.BookingHostContact.None);
        var english = Casazen.Infrastructure.Email.Templates.EmailTemplates.GuestBookingModified(
            new System.Globalization.CultureInfo("en"),
            "Giulia",
            summary,
            refundedAmount: 0m,
            chargedAmount: 80m,
            paymentLinkFollows: true,
            Casazen.Infrastructure.Email.Templates.BookingHostContact.None);

        Assert.Contains("Prenotazione modificata", italian.Subject, StringComparison.Ordinal);
        Assert.Contains("rimborsiamo", italian.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Casa &lt;del Lago&gt;", italian.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<del Lago>", italian.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Booking changed", english.Subject, StringComparison.Ordinal);
        Assert.Contains("has been charged", english.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("payment link", english.HtmlBody, StringComparison.Ordinal);
    }

    // --- The harness -----------------------------------------------------------------------------------------------------------

    /// <summary>The tenant of an authenticated request of an org that is not the booking's.</summary>
    private sealed class OtherOrgTenant : Core.Multitenancy.ITenantContext
    {
        public Guid? OrgId { get; } = Guid.Parse("11111111-2222-4333-8444-555555555555");

        public bool FilterEnabled => true;
    }

    private sealed class Harness
    {
        public string Database { get; } = $"booking-edit-{Guid.NewGuid():N}";

        public Mock<IStripeService> Stripe { get; } = new(MockBehavior.Strict);

        public Mock<IBookingService> Pricing { get; } = new();

        public Mock<IBookingPaymentLinkService> Links { get; } = new(MockBehavior.Strict);

        public Mock<IEmailQueue> Emails { get; } = new();

        private Guid PropertyId { get; } = Guid.NewGuid();

        public AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Database).Options);

        public HostBookingService Service(AppDbContext db)
        {
            var configuration = new ConfigurationBuilder().Build();
            var notifier = new BookingNotifier(
                db, Emails.Object, EmailTestHelpers.Links(), Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance);
            return new HostBookingService(
                db,
                Pricing.Object,
                new BookingRepository(db),
                Mock.Of<ICheckoutHoldExpiryService>(),
                ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), configuration),
                Mock.Of<IOnSiteBookingRequestService>(),
                NullLogger<HostBookingService>.Instance,
                Clock,
                domainEvents: null,
                channelPolicy: null,
                refundService: new PaymentRefundService(
                    db, Stripe.Object, Mock.Of<IPaymentRefundRetryScheduler>(), Emails.Object, NullLogger<PaymentRefundService>.Instance, Clock),
                savedCardCharges: new DeferredChargeService(db, Stripe.Object, notifier, configuration, NullLogger<DeferredChargeService>.Instance, Clock),
                paymentLinks: Links.Object,
                notifier: notifier);
        }

        /// <summary>The refunds the test lets Stripe make: each succeeds at once and is recorded.</summary>
        public List<StripeRefundCreateRequest> AllowRefunds()
        {
            var requests = new List<StripeRefundCreateRequest>();
            Stripe
                .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((StripeRefundCreateRequest request, CancellationToken _) =>
                {
                    requests.Add(request);
                    return new Stripe.Refund { Id = "re_1", PaymentIntentId = request.PaymentIntentId, Amount = request.AmountCents, Status = "succeeded" };
                });
            return requests;
        }

        /// <summary>The price the edit comes to, whatever the stay: <paramref name="total"/> without tourist tax.</summary>
        public void PriceOf(decimal total) =>
            Pricing
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

        public HostBookingUpdate GuestsEdit(Guid bookingId, int guests, string? notes = null) =>
            new(
                bookingId,
                new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 12, 13, 0, 0, 0, DateTimeKind.Utc),
                guests,
                0,
                null,
                notes ?? "Arriviamo tardi",
                null,
                "auth0|host");

        public Payment StripePayment(string paymentIntentId, decimal amount) => new()
        {
            OrgId = OrgId,
            Amount = amount,
            Status = PaymentStatus.Completed,
            Method = Core.Entities.PaymentMethod.CreditCard,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            StripeAccountId = Account,
            ProcessedAt = Clock.GetUtcNow().UtcDateTime.AddDays(-3),
            CreatedAt = Clock.GetUtcNow().UtcDateTime.AddDays(-3),
            UpdatedAt = Clock.GetUtcNow().UtcDateTime.AddDays(-3),
        };

        /// <summary>A confirmed booking of the booking site, 10 to 13 December 2026, 2 guests, 400 in all (no tourist tax).</summary>
        public async Task<(Guid BookingId, Guid PaymentId)> SeedAsync(Payment[]? payments = null, Action<Booking>? configure = null)
        {
            await using var db = NewDb();
            var org = new OrgEntity { Id = OrgId, Name = "Casa Org", Slug = "casa-org", StripeConnectedAccountId = Account, ConnectChargesEnabled = true };
            var property = new Property
            {
                Id = PropertyId,
                OrgId = OrgId,
                OwnerId = "auth0|host",
                Name = "Casa del Lago",
                Address = "Via del Lago 1",
                City = "Como",
                MaxGuests = 4,
                Currency = "EUR",
            };
            var guest = new Guest { OrgId = OrgId, FirstName = "Giulia", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 12, 13, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 2,
                NumberOfAdults = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                SpecialRequests = "Arriviamo tardi",
                BasePrice = 400m,
                TotalPrice = 400m,
                BookingCode = "K7Q2M9X4TB",
                UpdatedAt = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
            };
            configure?.Invoke(booking);
            db.Orgs.Add(org);
            db.Properties.Add(property);
            db.Guests.Add(guest);
            db.Bookings.Add(booking);
            foreach (var payment in payments ?? [])
            {
                payment.BookingId = booking.Id;
                db.Payments.Add(payment);
            }

            await db.SaveChangesAsync();
            return (booking.Id, payments?.FirstOrDefault()?.Id ?? Guid.Empty);
        }

        public async Task<Booking> ReadBookingAsync(Guid bookingId)
        {
            await using var db = NewDb();
            return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        }

        public async Task<Payment> ReadPaymentAsync(Guid paymentId)
        {
            await using var db = NewDb();
            return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
        }

        public async Task<List<Payment>> ReadPaymentsAsync()
        {
            await using var db = NewDb();
            return await db.Payments.AsNoTracking().ToListAsync();
        }

        public async Task<List<PaymentRefund>> ReadRefundsAsync()
        {
            await using var db = NewDb();
            return await db.PaymentRefunds.AsNoTracking().ToListAsync();
        }
    }
}
