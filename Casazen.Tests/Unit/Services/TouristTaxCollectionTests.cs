using System.Globalization;
using System.Net;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Casazen.Tests.Unit.Push;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The tourist tax paid online in the guest check-in, never twice (E25/F01/US01/T01, NF-119, decision O12): the proofs of its money and permission rules,
/// on the pure rules and on the real collection service with the in-memory database, a fake payment provider and the calculation of the stay given.
/// </summary>
public class TouristTaxCollectionTests
{
    private const string Account = "acct_test_tourist_tax";
    private const string PublishableKey = "pk_test_casazen";
    private const string SwissMunicipalityCode = "5192";

    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-11-06T09:00:00Z", CultureInfo.InvariantCulture));
    private static readonly DateTime CheckIn = new(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc);

    // ─── What is due (pure, to the cent) ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TouristTaxDue_ChannelCollects_Zero_Test()
    {
        // The channel collects the tax and pays the comune: nothing to collect and nothing to pay online, whatever the stay owes.
        var due = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Channel, amountDue: 18.00m));

        Assert.Equal(TouristTaxCollectionStatus.CollectedByChannel, due.Status);
        Assert.Equal(TouristTaxNotPayableReason.ChannelCollects, due.Reason);
        Assert.Equal(0m, due.ToCollect);
        Assert.Equal(0m, due.OnlinePayable);
        Assert.Equal(0m, due.RefundDue);

        // A channel stay nobody knows the collector of is never charged either (the channel may have charged it already).
        var unknown = TouristTaxCollectionRules.Evaluate(Facts(null, amountDue: 18.00m));
        Assert.Equal(TouristTaxNotPayableReason.CollectorUnknown, unknown.Reason);
        Assert.Equal(0m, unknown.OnlinePayable);

        // A tax that cannot be calculated is never taken as zero, nor charged: the host tells the amount.
        var notComputable = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: null));
        Assert.Equal(TouristTaxNotPayableReason.NotComputable, notComputable.Reason);
        Assert.Equal(TouristTaxCollectionStatus.ToCollect, notComputable.Status);
        Assert.Equal(0m, notComputable.OnlinePayable);
    }

    [Fact]
    public void TouristTaxDue_DirectIncluded_OnlyDifference_Test()
    {
        // €12.00 included in the price the guest paid with the booking, €15.00 confirmed: only €3.00 is payable.
        var higher = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: 15.00m, confirmed: 15.00m, included: 12.00m));
        Assert.Equal(TouristTaxCollectionStatus.ToCollect, higher.Status);
        Assert.Equal(3.00m, higher.ToCollect);
        Assert.Equal(3.00m, higher.OnlinePayable);
        Assert.Equal(0m, higher.RefundDue);

        // Before the host confirms the tax of the stay nothing more is asked of a guest who paid it with the booking.
        var waiting = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: 15.00m, confirmed: null, included: 12.00m));
        Assert.Equal(TouristTaxCollectionStatus.IncludedInBookingPayment, waiting.Status);
        Assert.Equal(TouristTaxNotPayableReason.AwaitingConfirmation, waiting.Reason);
        Assert.Equal(0m, waiting.OnlinePayable);

        // A lower confirmed amount is a difference to give back (the host's «To do»), never a refund made by itself and never a charge.
        var lower = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: 9.00m, confirmed: 9.00m, included: 12.00m));
        Assert.Equal(TouristTaxCollectionStatus.IncludedInBookingPayment, lower.Status);
        Assert.Equal(0m, lower.OnlinePayable);
        Assert.Equal(3.00m, lower.RefundDue);

        // The same amount: nothing to pay, nothing to give back.
        var same = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: 12.00m, confirmed: 12.00m, included: 12.00m));
        Assert.Equal((0m, 0m), (same.OnlinePayable, same.RefundDue));
    }

    [Fact]
    public void TouristTaxDue_PaidOnlineOnce_TheRestAtTheProperty_AndAStayWithNoNightGivesItBack()
    {
        // What is due less what was paid online; once paid, more owed is collected at the property (one online payment per stay).
        var paid = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: 15.00m, collected: 15.00m));
        Assert.Equal(TouristTaxCollectionStatus.PaidOnline, paid.Status);
        Assert.Equal(0m, paid.ToCollect);

        var more = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: 18.00m, collected: 15.00m));
        Assert.Equal(3.00m, more.ToCollect);
        Assert.Equal(0m, more.OnlinePayable);
        Assert.Equal(TouristTaxNotPayableReason.AlreadyPaidOnline, more.Reason);

        var less = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: 12.00m, collected: 15.00m));
        Assert.Equal(3.00m, less.RefundDue);

        // A no-show or a stay cancelled before any night owes nothing: what was paid online goes back in full.
        var ended = TouristTaxCollectionRules.Evaluate(Facts(TouristTaxCollector.Host, amountDue: 15.00m, collected: 15.00m) with
        {
            StayActive = false,
            EndedBeforeAnyNight = true,
        });
        Assert.Equal(TouristTaxCollectionStatus.NotDue, ended.Status);
        Assert.Equal(15.00m, ended.RefundDue);
        Assert.Equal(0m, ended.OnlinePayable);
    }

    [Fact]
    public void EndedBeforeAnyNight_ANoShowOrACancellationBeforeTheCheckInDay_ButNotAfterTheArrival()
    {
        var noShow = new Booking { Status = BookingStatus.Confirmed, CheckInDate = CheckIn, NoShowAt = CheckIn.AddHours(20) };
        var cancelledBefore = new Booking { Status = BookingStatus.Cancelled, CheckInDate = CheckIn, CancelledAt = CheckIn.AddDays(-3) };
        var cancelledAfterArrival = new Booking
        {
            Status = BookingStatus.Cancelled,
            CheckInDate = CheckIn,
            CancelledAt = CheckIn.AddDays(1),
            ArrivedAt = CheckIn.AddHours(15),
        };
        var cancelledLater = new Booking { Status = BookingStatus.Cancelled, CheckInDate = CheckIn, CancelledAt = CheckIn.AddDays(2) };
        var active = new Booking { Status = BookingStatus.Confirmed, CheckInDate = CheckIn };

        Assert.True(TouristTaxCollectionRules.EndedBeforeAnyNight(noShow, null));
        Assert.True(TouristTaxCollectionRules.EndedBeforeAnyNight(cancelledBefore, null));
        Assert.False(TouristTaxCollectionRules.EndedBeforeAnyNight(cancelledAfterArrival, null));
        Assert.False(TouristTaxCollectionRules.EndedBeforeAnyNight(cancelledLater, null));
        Assert.False(TouristTaxCollectionRules.EndedBeforeAnyNight(active, null));
    }

    // ─── The webhook: the amount of the intent, once ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TouristTaxWebhook_AmountFromIntentOnce_Test()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db);
        var record = await SeedRecordAsync(db, world.Booking, intentId: "pi_tax_once");
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 12.00m)));

        // An intent with the stay's markers on another account is not its payment: nothing is recorded.
        var foreign = await rig.Service.ApplyPaymentSucceededAsync(IntentEvent("pi_tax_once", "acct_someone_else", world.Booking.Id, record.Id, 1500));
        Assert.Null(foreign);
        Assert.Equal(0m, (await ReloadAsync(db, record.Id)).CollectedAmount);

        // The amount and the currency are the intent's (15.00 received), never the stay's own figure (12.00) nor a client's.
        var paidAt = new DateTime(2026, 11, 7, 10, 30, 0, DateTimeKind.Utc);
        var notice = await rig.Service.ApplyPaymentSucceededAsync(IntentEvent("pi_tax_once", Account, world.Booking.Id, record.Id, 1500, paidAt));
        Assert.NotNull(notice);

        // The same payment delivered again (or by the other endpoint) records nothing more.
        var again = await rig.Service.ApplyPaymentSucceededAsync(IntentEvent("pi_tax_once", Account, world.Booking.Id, record.Id, 1500, paidAt.AddMinutes(5)));
        Assert.Null(again);

        var stored = await ReloadAsync(db, record.Id);
        Assert.Equal(TouristTaxCollectionStatus.PaidOnline, stored.CollectionStatus);
        Assert.Equal(15.00m, stored.CollectedAmount);
        Assert.Equal("EUR", stored.Currency);
        Assert.Equal(paidAt, stored.CollectedAt);

        // The hosts of the booking hear of it once the event is committed: the bell, the pop-up and the open pages, through the push.
        await rig.Service.CompletePaidAsync(notice);
        var push = Assert.Single(rig.Pushes.Queued);
        Assert.Equal(PushTypes.TouristTaxPaid, push.Payload.Type);
        Assert.Equal(PushAudience.BookingHosts(world.Booking.Id), push.Audience);
        Assert.Equal(PushDeliveryKeys.TouristTaxPaid(record.Id, "pi_tax_once"), push.DeliveryKey);

        // 15.00 paid for 12.00 due: the difference is owed back, as a «To do» of the host, once.
        await rig.Service.ReconcileAsync(world.Booking.Id);
        var todo = Assert.Single(await db.InternalActivities.Where(a => a.EntityId == world.Booking.Id).ToListAsync());
        Assert.Equal(nameof(ActivityType.TouristTaxRefundDue), todo.ActivityType);
        Assert.Equal(ActivityAction.ReviewBooking, todo.Action);
        Assert.Equal(TouristTaxCollectionService.RefundDifferenceReason, todo.Reason);
    }

    [Fact]
    public async Task TouristTaxPayment_NoPaymentRowCreated_Test()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 9.00m, 6.00m)));
        Dictionary<string, string>? metadata = null;
        string? idempotencyKey = null;
        rig.Stripe
            .Setup(s => s.CreateConnectedAccountPaymentIntentAsync(
                Account, 1500, "eur", It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, long, string, Dictionary<string, string>, string, string?, CancellationToken>((_, _, _, meta, key, _, _) =>
            {
                metadata = meta;
                idempotencyKey = key;
            })
            .ReturnsAsync(new PaymentIntent
            {
                Id = "pi_tax_flow",
                ClientSecret = "pi_tax_flow_secret",
                Amount = 1500,
                Currency = "eur",
                Status = "requires_payment_method",
            });

        // The guest asks to pay: one intent on the organization's connected account, the publishable key of the platform and the account.
        var session = await rig.Service.CreatePaymentIntentAsync(world.Token);
        Assert.NotNull(session);
        Assert.Equal((15.00m, "EUR", Account, PublishableKey), (session.Amount, session.Currency, session.StripeAccountId, session.PublishableKey));
        Assert.Equal("pi_tax_flow_secret", session.ClientSecret);
        var record = await db.StayTouristTaxes.AsNoTracking().SingleAsync(t => t.BookingId == world.Booking.Id);
        Assert.NotNull(metadata);
        Assert.Equal(TouristTaxCharges.Kind, metadata[TouristTaxCharges.KindMetadataKey]);
        Assert.Equal(world.Booking.Id.ToString(), metadata[TouristTaxCharges.BookingMetadataKey]);
        Assert.Equal(record.Id.ToString(), metadata[TouristTaxCharges.RecordMetadataKey]);
        Assert.Equal($"tourist-tax:{record.Id:N}:1500:first", idempotencyKey);
        Assert.Null(record.ConfirmedAt);

        // Stripe tells the Connect endpoint the guest paid: the webhook records it on the stay's tourist tax.
        var handler = WebhookHandler(db, rig.Service);
        await handler.HandleEventAsync(SucceededEvent("evt_tax_flow", "pi_tax_flow", metadata, 1500), WebhookSource.Connected);

        var paid = await ReloadAsync(db, record.Id);
        Assert.Equal(TouristTaxCollectionStatus.PaidOnline, paid.CollectionStatus);
        Assert.Equal(15.00m, paid.CollectedAmount);

        // No Payment row, ever: the owner ledger, the balance of the booking and the revenue do not change.
        Assert.Empty(await db.Payments.AsNoTracking().ToListAsync());
        Assert.Equal(PushTypes.TouristTaxPaid, Assert.Single(rig.Pushes.Queued).Payload.Type);

        // Paid: nothing more can be paid online, so no second intent is created.
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.CreatePaymentIntentAsync(world.Token));
        Assert.Equal(TouristTaxCharges.NothingToPayCode, refused.Code);
        rig.Stripe.Verify(
            s => s.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── The link of the guest reaches its own stay only ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TouristTaxPublic_OtherBookingToken404_Test()
    {
        await using var db = NewDb();
        var first = await SeedAsync(db);
        var other = await SeedAsync(db);
        var expired = await SeedAsync(db, sessionExpiresAt: Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
        var cancelled = await SeedAsync(db, status: BookingStatus.Cancelled);
        var rig = Rig(
            db,
            (first.Booking.Id, Calculated(first.Booking.Id, 15.00m)),
            (other.Booking.Id, Calculated(other.Booking.Id, 9.00m)),
            (expired.Booking.Id, Calculated(expired.Booking.Id, 6.00m)),
            (cancelled.Booking.Id, Calculated(cancelled.Booking.Id, 3.00m)));

        // Each link reads its own stay's figures, never another's.
        Assert.Equal(15.00m, (await rig.Service.GetForGuestAsync(first.Token))!.Payable);
        Assert.Equal(9.00m, (await rig.Service.GetForGuestAsync(other.Token))!.Payable);

        // An unknown link, an expired one and the link of a cancelled stay reach nothing: 404, and no intent is ever asked for.
        Assert.Null(await rig.Service.GetForGuestAsync("not-a-link-of-any-stay"));
        Assert.Null(await rig.Service.GetForGuestAsync(expired.Token));
        Assert.Null(await rig.Service.GetForGuestAsync(cancelled.Token));
        Assert.Null(await rig.Service.CreatePaymentIntentAsync(expired.Token));

        var controller = PublicController(rig.Service);
        Assert.IsType<NotFoundResult>((await controller.GetTouristTax("not-a-link-of-any-stay")).Result);
        Assert.IsType<NotFoundResult>((await controller.CreateTouristTaxPaymentIntent(cancelled.Token)).Result);
        rig.Stripe.Verify(
            s => s.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TouristTaxPublic_OrganizationThatDoesNotOfferIt_409()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db, offered: false);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 15.00m)));

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.GetForGuestAsync(world.Token));
        Assert.Equal(TouristTaxCharges.NotAvailableCode, refused.Code);
        await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.CreatePaymentIntentAsync(world.Token));
    }

    // ─── What the price included: only what CasaZen collected with it ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TouristTaxDue_IncludedInThePrice_OnlyForADirectBookingThatCasaZenWasPaidFor_Test()
    {
        await using var db = NewDb();
        var paid = await SeedAsync(db, source: BookingSource.Direct, touristTaxAmount: 12.00m, payment: PaymentStatus.Completed);
        var partlyRefunded = await SeedAsync(db, source: BookingSource.Direct, touristTaxAmount: 12.00m, payment: PaymentStatus.PartiallyRefunded);
        var deferred = await SeedAsync(
            db, source: BookingSource.Direct, touristTaxAmount: 12.00m, paymentOption: PaymentOption.OnCancellationDeadline, payment: PaymentStatus.Pending);
        var refunded = await SeedAsync(db, source: BookingSource.Direct, touristTaxAmount: 12.00m, payment: PaymentStatus.Refunded);
        var atTheProperty = await SeedAsync(db, source: BookingSource.Direct, touristTaxAmount: 12.00m, paymentOption: PaymentOption.OnSite);
        var manual = await SeedAsync(db, source: BookingSource.Manual, touristTaxAmount: 12.00m, payment: PaymentStatus.Completed);
        var cases = new (StayWorld World, decimal Payable, decimal Included)[]
        {
            // The price of a direct booking that went through CasaZen included 12.00: only the part of the confirmed 15.00 above it is payable.
            (paid, 3.00m, 12.00m),
            (partlyRefunded, 3.00m, 12.00m),

            // Not collected by CasaZen: the charge of the deadline not taken yet, a payment given back, a booking paid at the property, the quote of a host's
            // manual booking. The amount written on the booking is no tax paid: the whole 15.00 is payable and nothing is owed back.
            (deferred, 15.00m, 0m),
            (refunded, 15.00m, 0m),
            (atTheProperty, 15.00m, 0m),
            (manual, 15.00m, 0m),
        };
        foreach (var (world, _, _) in cases)
            await SeedStayRecordAsync(db, world.Booking, confirmedAmount: 15.00m);

        var rig = Rig(db, cases.Select(c => (c.World.Booking.Id, Calculated(c.World.Booking.Id, 7.50m, 7.50m))).ToArray());

        foreach (var (world, payable, included) in cases)
        {
            Assert.Equal(payable, (await rig.Service.GetForGuestAsync(world.Token))!.Payable);
            Assert.Equal(included, (await rig.Service.GetForHostAsync(world.Booking.Id, null)).IncludedInBooking);
        }
    }

    [Fact]
    public async Task TouristTaxDue_ChannelStay_TheChannelRuleAndTheHostsDeclarationDecideWhoCollects_NotTheFigureOfTheChannel_Test()
    {
        await using var db = NewDb();
        var ruled = await SeedAsync(db, source: BookingSource.Airbnb, touristTaxAmount: 8.00m);
        var unknown = await SeedAsync(db, source: BookingSource.BookingCom, touristTaxAmount: 8.00m);
        var declared = await SeedAsync(db, source: BookingSource.Expedia, touristTaxAmount: 8.00m);
        db.TouristTaxChannelRules.Add(new TouristTaxChannelRule
        {
            Channel = BookingSource.Airbnb,
            IstatCode = "013075",
            Collector = TouristTaxCollector.Channel,
            SourceUrl = "https://example.test/airbnb-como",
        });
        await db.SaveChangesAsync();
        await SeedStayRecordAsync(db, declared.Booking, declaredCollector: TouristTaxCollector.Host);
        var rig = Rig(
            db,
            (ruled.Booking.Id, Calculated(ruled.Booking.Id, 7.50m, 7.50m)),
            (unknown.Booking.Id, Calculated(unknown.Booking.Id, 7.50m, 7.50m)),
            (declared.Booking.Id, Calculated(declared.Booking.Id, 7.50m, 7.50m)));

        // The channel collects it (the active rule of the channel in the comune): nothing to pay whatever the stay owes, and no intent is ever asked for.
        var viaChannel = (await rig.Service.GetForGuestAsync(ruled.Token))!;
        Assert.Equal(
            (TouristTaxCollectionStatus.CollectedByChannel, TouristTaxNotPayableReason.ChannelCollects, 0m),
            (viaChannel.Status, viaChannel.Reason, viaChannel.Payable));
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.CreatePaymentIntentAsync(ruled.Token));
        Assert.Equal(TouristTaxCharges.NothingToPayCode, refused.Code);

        // Nobody knows who collected it (no rule, no declaration): never charged, the channel may have charged it already.
        var notKnown = (await rig.Service.GetForGuestAsync(unknown.Token))!;
        Assert.Equal((TouristTaxNotPayableReason.CollectorUnknown, 0m), (notKnown.Reason, notKnown.Payable));
        await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.CreatePaymentIntentAsync(unknown.Token));

        // The host declared that it collects: the whole tax is payable, and the figure the channel reported (8.00) is not «already paid with the booking».
        var hostCollects = (await rig.Service.GetForGuestAsync(declared.Token))!;
        Assert.Equal((TouristTaxCollectionStatus.ToCollect, 15.00m), (hostCollects.Status, hostCollects.Payable));
        Assert.Equal(0m, (await rig.Service.GetForHostAsync(declared.Booking.Id, null)).IncludedInBooking);
    }

    // ─── The tax confirmed lower than what was collected: the «To do» at the change, not at a later event ──────────────────────────

    [Fact]
    public async Task TouristTaxConfirmation_LowerThanWhatThePriceIncluded_RaisesTheRefundTodoRightAway_Test()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db, source: BookingSource.Direct, touristTaxAmount: 12.00m, payment: PaymentStatus.Completed);
        await SeedStayRecordAsync(db, world.Booking, confirmedAmount: 9.00m);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 4.50m, 4.50m)));

        // The host confirmed 9.00 for a price that included 12.00: the confirmation records no event, so the change itself evaluates the collection.
        await rig.Service.ReconcileAfterChangeAsync(world.Booking.Id);

        var todo = Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == world.Booking.Id).ToListAsync());
        Assert.Equal(nameof(ActivityType.TouristTaxRefundDue), todo.ActivityType);
        Assert.Equal(ActivityAction.ReviewBooking, todo.Action);
        Assert.Equal(TouristTaxCollectionService.RefundDifferenceReason, todo.Reason);
        Assert.Equal(3.00m, (await rig.Service.GetForHostAsync(world.Booking.Id, null)).RefundDue);

        // Never a charge, and once per booking: the same change again raises nothing new.
        Assert.Equal(0m, (await rig.Service.GetForGuestAsync(world.Token))!.Payable);
        await rig.Service.ReconcileAfterChangeAsync(world.Booking.Id);
        Assert.Single(await db.InternalActivities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TouristTaxConfirmation_OfABookingCasaZenCollectedNothingFor_RaisesNoRefundTodo()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db, source: BookingSource.Manual, touristTaxAmount: 12.00m);
        await SeedStayRecordAsync(db, world.Booking, confirmedAmount: 9.00m);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 4.50m, 4.50m)));

        // Nothing was collected with the price: a confirmed amount below the quote owes nothing back.
        await rig.Service.ReconcileAfterChangeAsync(world.Booking.Id);

        Assert.Empty(await db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Equal(0m, (await rig.Service.GetForHostAsync(world.Booking.Id, null)).RefundDue);
    }

    [Fact]
    public async Task TouristTaxConfirmation_WithTheFlagOff_EvaluatesNothing()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db, source: BookingSource.Direct, touristTaxAmount: 12.00m, payment: PaymentStatus.Completed);
        await SeedStayRecordAsync(db, world.Booking, confirmedAmount: 9.00m);
        var rig = RigWith(db, new RigOptions(FlagOn: false), (world.Booking.Id, Calculated(world.Booking.Id, 4.50m, 4.50m)));

        await rig.Service.ReconcileAfterChangeAsync(world.Booking.Id);

        Assert.Empty(await db.InternalActivities.AsNoTracking().ToListAsync());
    }

    // ─── A stay that ends: the refund, never a Payment row ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TouristTaxCancellation_BeforeAnyNight_RefundsTheOnlineTaxOnTheConnectedAccount()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db, status: BookingStatus.Cancelled, cancelledAt: CheckIn.AddDays(-5));
        var record = await SeedRecordAsync(db, world.Booking, intentId: "pi_tax_refund", collected: 15.00m);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 15.00m)));
        StripeRefundCreateRequest? asked = null;
        rig.Stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<StripeRefundCreateRequest, CancellationToken>((request, _) => asked = request)
            .ReturnsAsync(new Refund { Id = "re_tax", Status = "succeeded" });

        await rig.Service.ApplyStayEndedAsync(world.Booking.Id, CheckIn.AddDays(-5));
        // Delivered again: nothing is held any more, nothing is refunded twice.
        await rig.Service.ApplyStayEndedAsync(world.Booking.Id, CheckIn.AddDays(-5));

        Assert.NotNull(asked);
        Assert.Equal(("pi_tax_refund", Account, 1500L), (asked.PaymentIntentId, asked.ConnectedAccountId, asked.AmountCents));
        Assert.Equal($"tourist-tax-refund:{record.Id:N}:pi_tax_refund", asked.IdempotencyKey);
        rig.Stripe.Verify(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        var stored = await ReloadAsync(db, record.Id);
        Assert.Equal(TouristTaxCollectionStatus.NotDue, stored.CollectionStatus);
        Assert.Equal(15.00m, stored.RefundedAmount);
        Assert.Empty(await db.Payments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.InternalActivities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TouristTaxCancellation_AfterTheArrival_IsTheHostsDecision_NoAutomaticRefund()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db, status: BookingStatus.Cancelled, cancelledAt: CheckIn.AddDays(1), arrivedAt: CheckIn.AddHours(15));
        await SeedRecordAsync(db, world.Booking, intentId: "pi_tax_partial", collected: 15.00m);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 15.00m)));

        await rig.Service.ApplyStayEndedAsync(world.Booking.Id, CheckIn.AddDays(1));

        rig.Stripe.Verify(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        var todo = Assert.Single(await db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Equal(nameof(ActivityType.TouristTaxRefundDue), todo.ActivityType);
        Assert.Equal(TouristTaxCollectionService.PartialStayReason, todo.Reason);
    }

    [Fact]
    public async Task TouristTaxCancellation_APaymentOnItsWay_IsWaitedFor()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db, status: BookingStatus.Cancelled, cancelledAt: CheckIn.AddDays(-5));
        await SeedRecordAsync(db, world.Booking, intentId: "pi_tax_moving");
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 15.00m)));
        rig.Stripe
            .Setup(s => s.GetPaymentIntentAsync("pi_tax_moving", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_tax_moving", Status = "succeeded", Amount = 1500 });

        // The success is not recorded yet (events are not in order): the event is delivered again, never consumed with no effect.
        await Assert.ThrowsAsync<TouristTaxCollectionPendingException>(() => rig.Service.ApplyStayEndedAsync(world.Booking.Id, CheckIn.AddDays(-5)));
        rig.Stripe.Verify(
            s => s.CancelPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── «Pagata in struttura»: never twice ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TouristTaxPaidAtProperty_CancelsTheOpenIntent_SoTheGuestCannotPayTwice()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db);
        var record = await SeedRecordAsync(db, world.Booking, intentId: "pi_tax_open");
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 15.00m)));
        rig.Stripe
            .Setup(s => s.GetPaymentIntentAsync("pi_tax_open", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_tax_open", Status = "requires_payment_method", Amount = 1500, Currency = "eur" });

        var view = await rig.Service.RecordPaidAtPropertyAsync(world.Booking.Id, "auth0|host");

        Assert.Equal(TouristTaxCollectionStatus.PaidAtProperty, view.Status);
        Assert.Equal(0m, view.OnlinePayable);
        rig.Stripe.Verify(
            s => s.CancelPaymentIntentAsync("pi_tax_open", Account, $"tourist-tax-at-property:{record.Id:N}:pi_tax_open", It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(TouristTaxNotPayableReason.PaidAtProperty, (await rig.Service.GetForGuestAsync(world.Token))!.Reason);
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.CreatePaymentIntentAsync(world.Token));
        Assert.Equal(TouristTaxCharges.NothingToPayCode, refused.Code);
    }

    // ─── The lock code: held back only while the guest owes a tax it can pay online ──────────────────────────────────────────────────

    [Fact]
    public async Task TouristTaxAccessCode_HeldBackOnlyWhileTheGuestOwesATaxItCanPayOnline_Test()
    {
        await using var db = NewDb();
        var due = await SeedAsync(db, requireTaxBeforeCode: true);
        var notAsked = await SeedAsync(db);
        var paidOnline = await SeedAsync(db, requireTaxBeforeCode: true);
        await SeedRecordAsync(db, paidOnline.Booking, intentId: "pi_tax_code_paid", collected: 15.00m);
        var paidAtTheProperty = await SeedAsync(db, requireTaxBeforeCode: true);
        await SeedStayRecordAsync(db, paidAtTheProperty.Booking, paidAtProperty: true);
        var exempt = await SeedAsync(db, requireTaxBeforeCode: true);
        var viaChannel = await SeedAsync(db, requireTaxBeforeCode: true, source: BookingSource.Airbnb);
        var notComputable = await SeedAsync(db, requireTaxBeforeCode: true);
        var accountNotReady = await SeedAsync(db, requireTaxBeforeCode: true, chargesEnabled: false);
        db.TouristTaxChannelRules.Add(new TouristTaxChannelRule
        {
            Channel = BookingSource.Airbnb,
            IstatCode = "013075",
            Collector = TouristTaxCollector.Channel,
            SourceUrl = "https://example.test/airbnb-como",
        });
        await db.SaveChangesAsync();
        var rig = Rig(
            db,
            (due.Booking.Id, Calculated(due.Booking.Id, 7.50m, 7.50m)),
            (notAsked.Booking.Id, Calculated(notAsked.Booking.Id, 7.50m, 7.50m)),
            (paidOnline.Booking.Id, Calculated(paidOnline.Booking.Id, 7.50m, 7.50m)),
            (paidAtTheProperty.Booking.Id, Calculated(paidAtTheProperty.Booking.Id, 7.50m, 7.50m)),
            (exempt.Booking.Id, Calculated(exempt.Booking.Id, 0m, 0m)),
            (viaChannel.Booking.Id, Calculated(viaChannel.Booking.Id, 7.50m, 7.50m)),
            (accountNotReady.Booking.Id, Calculated(accountNotReady.Booking.Id, 7.50m, 7.50m)));

        // The tax is due, unpaid, and the guest can pay it online now: the code is held back.
        Assert.True(await rig.Service.IsAccessCodeHeldBackAsync(due.Booking.Id));

        // Every other case lets the code go: the organization does not ask for it, it is paid online, it is recorded as paid at the property, every guest is
        // exempt (nothing is due), the channel collects it, nobody can calculate it, the account of the organization cannot take the payment.
        foreach (var world in new[] { notAsked, paidOnline, paidAtTheProperty, exempt, viaChannel, notComputable, accountNotReady })
            Assert.False(await rig.Service.IsAccessCodeHeldBackAsync(world.Booking.Id));

        // A guest who has no way to pay is never kept out: no platform key, or the flag off.
        var withoutKey = RigWith(db, new RigOptions(PublishableKeyIsSet: false), (due.Booking.Id, Calculated(due.Booking.Id, 7.50m, 7.50m)));
        Assert.False(await withoutKey.Service.IsAccessCodeHeldBackAsync(due.Booking.Id));
        var flagOff = RigWith(db, new RigOptions(FlagOn: false), (due.Booking.Id, Calculated(due.Booking.Id, 7.50m, 7.50m)));
        Assert.False(await flagOff.Service.IsAccessCodeHeldBackAsync(due.Booking.Id));

        // Recorded as paid at the property by the host: the code is released at the next ask.
        await rig.Service.RecordPaidAtPropertyAsync(due.Booking.Id, "auth0|host");
        Assert.False(await rig.Service.IsAccessCodeHeldBackAsync(due.Booking.Id));
    }

    // ─── The refunds: what the charge says, what the provider refuses ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TouristTaxChargeRefunded_FollowsTheCharge_NeverMoreThanWasPaid_AndWaitsForAPaymentNotRecordedYet_Test()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db);
        var record = await SeedRecordAsync(db, world.Booking, intentId: "pi_tax_refunded", collected: 15.00m);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 7.50m, 7.50m)));

        // The refunded amount of the stay is what the charge says in all (5.00 so far).
        Assert.True(await rig.Service.ApplyChargeRefundedAsync(new TouristTaxChargeRefund("pi_tax_refunded", Account, 500)));
        Assert.Equal(5.00m, (await ReloadAsync(db, record.Id)).RefundedAmount);

        // The same event again changes nothing, and an older total never takes back what a later one recorded.
        await rig.Service.ApplyChargeRefundedAsync(new TouristTaxChargeRefund("pi_tax_refunded", Account, 500));
        await rig.Service.ApplyChargeRefundedAsync(new TouristTaxChargeRefund("pi_tax_refunded", Account, 300));
        Assert.Equal(5.00m, (await ReloadAsync(db, record.Id)).RefundedAmount);

        // Never more than what was paid: a total above it is capped (the check constraint of the column would refuse it).
        await rig.Service.ApplyChargeRefundedAsync(new TouristTaxChargeRefund("pi_tax_refunded", Account, 2000));
        Assert.Equal(15.00m, (await ReloadAsync(db, record.Id)).RefundedAmount);

        // The event of another account that carries the intent of the stay is not its refund.
        var other = await SeedRecordAsync(db, (await SeedAsync(db)).Booking, intentId: "pi_tax_other_account", collected: 15.00m);
        Assert.True(await rig.Service.ApplyChargeRefundedAsync(new TouristTaxChargeRefund("pi_tax_other_account", "acct_someone_else", 1500)));
        Assert.Equal(0m, (await ReloadAsync(db, other.Id)).RefundedAmount);

        // A charge that is not a tourist tax's is left to its own handler (false), and a refund of a payment not recorded yet is delivered again.
        Assert.False(await rig.Service.ApplyChargeRefundedAsync(new TouristTaxChargeRefund("pi_of_a_booking", Account, 1500)));
        Assert.False(await rig.Service.ApplyChargeRefundedAsync(new TouristTaxChargeRefund(string.Empty, Account, 1500)));
        await SeedRecordAsync(db, (await SeedAsync(db)).Booking, intentId: "pi_tax_not_recorded");
        await Assert.ThrowsAsync<TouristTaxCollectionPendingException>(
            () => rig.Service.ApplyChargeRefundedAsync(new TouristTaxChargeRefund("pi_tax_not_recorded", Account, 500)));
    }

    [Fact]
    public async Task TouristTaxChargeRefunded_ThroughTheWebhook_UpdatesTheStay_AndABookingRefundStillReachesItsOwnHandler_Test()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db);
        var record = await SeedRecordAsync(db, world.Booking, intentId: "pi_tax_webhook_refund", collected: 15.00m);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 7.50m, 7.50m)));
        var refunds = new Mock<IPaymentRefundService>();
        var handler = WebhookHandler(db, rig.Service, refunds.Object);

        // The refund of a tax paid online: the stay follows the charge, never a Payment row, and the refunds of the bookings are not asked.
        await handler.HandleEventAsync(RefundedEvent("evt_tax_refund", "pi_tax_webhook_refund", 600), WebhookSource.Connected);

        Assert.Equal(6.00m, (await ReloadAsync(db, record.Id)).RefundedAmount);
        Assert.Empty(await db.Payments.AsNoTracking().ToListAsync());
        refunds.Verify(r => r.SyncPaymentIntentRefundsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // The refund of a charge that is not a tax's still reaches the refunds of the bookings, as it did before.
        await handler.HandleEventAsync(RefundedEvent("evt_booking_refund", "pi_of_a_booking", 1000), WebhookSource.Connected);
        refunds.Verify(r => r.SyncPaymentIntentRefundsAsync("pi_of_a_booking", Account, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TouristTaxCancellation_RefundRefusedByTheProvider_RaisesTheTodo_AndOneThatMayPassIsDeliveredAgain_Test()
    {
        await using var db = NewDb();
        var refused = await SeedAsync(db, status: BookingStatus.Cancelled, cancelledAt: CheckIn.AddDays(-5));
        var refusedRecord = await SeedRecordAsync(db, refused.Booking, intentId: "pi_tax_refused", collected: 15.00m);
        var transient = await SeedAsync(db, status: BookingStatus.Cancelled, cancelledAt: CheckIn.AddDays(-5));
        var transientRecord = await SeedRecordAsync(db, transient.Booking, intentId: "pi_tax_transient", collected: 15.00m);
        var rig = Rig(
            db,
            (refused.Booking.Id, Calculated(refused.Booking.Id, 7.50m, 7.50m)),
            (transient.Booking.Id, Calculated(transient.Booking.Id, 7.50m, 7.50m)));
        rig.Stripe
            .Setup(s => s.CreateRefundAsync(It.Is<StripeRefundCreateRequest>(r => r.PaymentIntentId == "pi_tax_refused"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.BadRequest, new StripeError { Code = "balance_insufficient" }, "Insufficient funds"));
        rig.Stripe
            .Setup(s => s.CreateRefundAsync(It.Is<StripeRefundCreateRequest>(r => r.PaymentIntentId == "pi_tax_transient"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException("Connection reset"));

        // A refusal that will not pass: the event is consumed, the host is asked to give the money back, and nothing says it was refunded.
        await rig.Service.ApplyStayEndedAsync(refused.Booking.Id, CheckIn.AddDays(-5));
        var todo = Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == refused.Booking.Id).ToListAsync());
        Assert.Equal(TouristTaxCollectionService.RefundFailedReason, todo.Reason);
        var stored = await ReloadAsync(db, refusedRecord.Id);
        Assert.Equal((0m, TouristTaxCollectionStatus.NotDue), (stored.RefundedAmount, stored.CollectionStatus));
        Assert.Equal(15.00m, (await rig.Service.GetForHostAsync(refused.Booking.Id, null)).RefundDue);

        // A failure that may pass (no answer): thrown again, so the outbox delivers the event again; nothing is recorded and nobody is asked yet.
        await Assert.ThrowsAsync<StripeException>(() => rig.Service.ApplyStayEndedAsync(transient.Booking.Id, CheckIn.AddDays(-5)));
        Assert.Equal(0m, (await ReloadAsync(db, transientRecord.Id)).RefundedAmount);
        Assert.Empty(await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == transient.Booking.Id).ToListAsync());
    }

    [Fact]
    public async Task TouristTaxNoShow_RefundsTheOnlineTaxInFull_AndIsNotDueInEveryReader_Test()
    {
        await using var db = NewDb();
        var world = await SeedAsync(db, noShowAt: CheckIn.AddHours(20));
        var record = await SeedRecordAsync(db, world.Booking, intentId: "pi_tax_noshow", collected: 15.00m);
        var rig = Rig(db, (world.Booking.Id, Calculated(world.Booking.Id, 7.50m, 7.50m)));
        rig.Stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Refund { Id = "re_noshow", Status = "succeeded" });

        // The guests never came: no night, the tax is not due and what was paid online goes back in full, once.
        await rig.Service.ApplyStayEndedAsync(world.Booking.Id, CheckIn.AddHours(20));
        await rig.Service.ApplyStayEndedAsync(world.Booking.Id, CheckIn.AddHours(20));

        var stored = await ReloadAsync(db, record.Id);
        Assert.Equal((TouristTaxCollectionStatus.NotDue, 15.00m), (stored.CollectionStatus, stored.RefundedAmount));
        rig.Stripe.Verify(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(await db.Payments.AsNoTracking().ToListAsync());

        // Not due in every reader: the page of the guest's link (the booking is still confirmed, with a no-show mark) and the host's block.
        var guest = (await rig.Service.GetForGuestAsync(world.Token))!;
        Assert.Equal((TouristTaxCollectionStatus.NotDue, 0m), (guest.Status, guest.Payable));
        var host = await rig.Service.GetForHostAsync(world.Booking.Id, null);
        Assert.Equal((TouristTaxCollectionStatus.NotDue, 0m, 0m), (host.Status, host.RefundDue, host.OnlinePayable));
        await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.CreatePaymentIntentAsync(world.Token));
    }

    // ─── Switzerland: the rule of the canton, in francs, when the host collects ───────────────────────────────────────────────────

    [Fact]
    public async Task TouristTaxDue_Switzerland_TheRuleOfTheCantonInFrancs_WhenTheHostCollects_Test()
    {
        await using var db = NewDb();
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = SwissMunicipalityCode, CantonCode = "TI", OfficialName = "Lugano" });
        await db.SaveChangesAsync();
        var swiss = new SwissTouristTaxOptions
        {
            Rules = [new SwissTouristTaxRuleOptions { Canton = "TI", RatePerPersonPerNight = 2.00m, ValidFrom = new DateOnly(2025, 1, 1), SourceUrl = "https://example.test/ti" }],
        };
        var hostCollects = await SeedAsync(db, swiss: true);
        var portalCollects = await SeedAsync(db, swiss: true, source: BookingSource.Airbnb);
        var rig = RigWith(db, new RigOptions(Swiss: swiss, PlatformCollected: [BookingSource.Airbnb]));
        rig.Stripe
            .Setup(s => s.CreateConnectedAccountPaymentIntentAsync(
                Account, 1200, "chf", It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_chf", ClientSecret = "pi_chf_secret", Amount = 1200, Currency = "chf", Status = "requires_payment_method" });

        // Two guests, three nights, CHF 2.00 a person and night: CHF 12.00, charged in francs on the connected account.
        var page = (await rig.Service.GetForGuestAsync(hostCollects.Token))!;
        Assert.Equal((12.00m, "CHF", 2), (page.Payable, page.Currency, page.Guests.Count));
        Assert.All(page.Guests, line => Assert.Equal(6.00m, line.Amount));
        var session = (await rig.Service.CreatePaymentIntentAsync(hostCollects.Token))!;
        Assert.Equal((12.00m, "CHF"), (session.Amount, session.Currency));

        // The portal that collects it itself (the default of the Swiss issuer): nothing to pay online, nothing is ever charged.
        var viaPortal = (await rig.Service.GetForGuestAsync(portalCollects.Token))!;
        Assert.Equal((TouristTaxCollectionStatus.CollectedByChannel, TouristTaxNotPayableReason.ChannelCollects, 0m), (viaPortal.Status, viaPortal.Reason, viaPortal.Payable));
        await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.CreatePaymentIntentAsync(portalCollects.Token));

        // A canton with no rule: not calculated, never an assumed zero and never a charge, whatever the guests owe.
        var withoutRule = RigWith(db, new RigOptions(Swiss: new SwissTouristTaxOptions()));
        var unknown = (await withoutRule.Service.GetForGuestAsync(hostCollects.Token))!;
        Assert.Equal((TouristTaxNotPayableReason.NotComputable, 0m, (decimal?)null), (unknown.Reason, unknown.Payable, unknown.AmountDue));
    }

    // ─── Registration and the consumer of the booking events ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddCasazenTouristTaxCollection_RegistersTheServiceAndTheConsumerOfTheBookingEvents()
    {
        var services = new ServiceCollection();

        services.AddCasazenTouristTaxCollection();

        Assert.Equal(
            [typeof(TouristTaxCollectionEventHandler)],
            services.Where(d => d.ServiceType == typeof(IEventHandler<DomainEvent>)).Select(d => d.ImplementationType).ToList());
        var service = Assert.Single(services, d => d.ServiceType == typeof(ITouristTaxCollectionService));
        Assert.Equal(typeof(TouristTaxCollectionService), service.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, service.Lifetime);
    }

    [Fact]
    public async Task TheConsumer_HearsTheEndAndTheChangesOfAStay_AndNothingWhileTheFlagIsOff()
    {
        var bookingId = Guid.NewGuid();
        var collection = new Mock<ITouristTaxCollectionService>();
        var flags = new TestFeatureFlags();
        var handler = new TouristTaxCollectionEventHandler(collection.Object, flags, NullLogger<TouristTaxCollectionEventHandler>.Instance);
        var occurredAt = CheckIn.AddDays(-2);

        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), Guid.NewGuid(), DomainEventType.BookingCancelled, bookingId, occurredAt, "{}"), CancellationToken.None);
        collection.VerifyNoOtherCalls();

        flags.Set(FeatureFlags.TouristTaxOnlinePayment, true);
        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), Guid.NewGuid(), DomainEventType.BookingCancelled, bookingId, occurredAt, "{}"), CancellationToken.None);
        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), Guid.NewGuid(), DomainEventType.BookingNoShowMarked, bookingId, occurredAt, "{}"), CancellationToken.None);
        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), Guid.NewGuid(), DomainEventType.CheckOutCompleted, bookingId, occurredAt, "{}"), CancellationToken.None);
        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), Guid.NewGuid(), DomainEventType.ReviewReceived, bookingId, occurredAt, "{}"), CancellationToken.None);

        collection.Verify(c => c.ApplyStayEndedAsync(bookingId, occurredAt, It.IsAny<CancellationToken>()), Times.Exactly(2));
        collection.Verify(c => c.ReconcileAsync(bookingId, It.IsAny<CancellationToken>()), Times.Once);
        collection.VerifyNoOtherCalls();
    }

    // ─── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static TouristTaxCollectionFacts Facts(
        TouristTaxCollector? collector,
        decimal? amountDue,
        decimal? confirmed = null,
        decimal included = 0m,
        decimal collected = 0m)
    {
        return new TouristTaxCollectionFacts(
            StayActive: true,
            EndedBeforeAnyNight: false,
            collector,
            amountDue,
            confirmed,
            included,
            TouristTaxCollectionStatus.ToCollect,
            collected,
            RefundedOnline: 0m);
    }

    /// <summary>The calculation of a stay of three nights the host collects, with the tax of each guest.</summary>
    private static StayTouristTaxView Calculated(Guid bookingId, params decimal[] perGuest) => new(
        bookingId,
        TouristTaxQuoteStatus.Calculated,
        TouristTaxRateSource.Official,
        MissingRate: false,
        Nights: 3,
        TaxableNights: 3,
        AgeRulesApply: false,
        Categories: [],
        Total: perGuest.Sum(),
        AmountDue: perGuest.Sum(),
        Collector: TouristTaxCollector.Host,
        CollectorSource: TouristTaxCollectorSource.Default,
        CollectorNeeded: false,
        Guests: perGuest.Select((amount, position) => new StayTouristTaxGuestView(position, 3, 3, amount, TouristTaxExemptionKind.None, null)).ToList(),
        Reasons: [],
        ConfirmedAt: null,
        Stale: false);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"tourist-tax-collection-{Guid.NewGuid():N}").Options);

    /// <summary>
    /// An org that offers the payment, with a connected account that takes payments, one property in Como, a booking the host entered (its price does not
    /// include the tax) and a check-in link of it.
    /// </summary>
    private static async Task<StayWorld> SeedAsync(
        AppDbContext db,
        BookingStatus status = BookingStatus.Confirmed,
        DateTime? cancelledAt = null,
        DateTime? arrivedAt = null,
        DateTime? sessionExpiresAt = null,
        bool offered = true,
        BookingSource source = BookingSource.Manual,
        decimal touristTaxAmount = 0m,
        PaymentOption paymentOption = PaymentOption.Immediate,
        PaymentStatus? payment = null,
        bool swiss = false,
        DateTime? noShowAt = null,
        bool requireTaxBeforeCode = false,
        bool chargesEnabled = true)
    {
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Org",
            Slug = $"org-{orgId:N}",
            DisplayName = "Org",
            StripeConnectedAccountId = Account,
            ConnectChargesEnabled = chargesEnabled,
            TouristTaxOnlinePaymentEnabled = offered,
            TouristTaxRequiredBeforeAccessCode = requireTaxBeforeCode,
        });
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "auth0|host",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = swiss ? "Lugano" : "Como",
            ComuneIstatCode = swiss ? null : "013075",
            Country = swiss ? "CH" : "IT",
            Currency = swiss ? "CHF" : "EUR",
            SwissMunicipalityCode = swiss ? SwissMunicipalityCode : null,
            PostalCode = swiss ? "6900" : "22100",
            NightlyRate = 100m,
            MaxGuests = 4,
        };
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Property = property,
            PropertyId = property.Id,
            Guest = guest,
            GuestId = guest.Id,
            CheckInDate = CheckIn,
            CheckOutDate = CheckIn.AddDays(3),
            Status = status,
            Source = source,
            PaymentOption = paymentOption,
            NumberOfGuests = 2,
            BasePrice = 300m,
            TouristTaxAmount = touristTaxAmount,
            TotalPrice = 300m + touristTaxAmount,
            CancelledAt = cancelledAt,
            ArrivedAt = arrivedAt,
            NoShowAt = noShowAt,
        };
        var token = $"token-{Guid.NewGuid():N}";
        db.AddRange(property, guest, booking);
        if (payment is { } paymentStatus)
        {
            // A payment of the checkout through CasaZen (Stripe) in the state asked: the tax of the price is collected only when it went through.
            db.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                OrgId = orgId,
                Amount = booking.TotalPrice,
                Status = paymentStatus,
                StripePaymentIntentId = $"pi_booking_{booking.Id:N}",
                TransactionId = $"pi_booking_{booking.Id:N}",
                RefundedAmount = paymentStatus == PaymentStatus.Refunded ? booking.TotalPrice : 0m,
            });
        }

        db.GuestCheckInSessions.Add(new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = orgId,
            TokenHash = GuestCheckInService.HashToken(token),
            ExpiresAt = sessionExpiresAt ?? Clock.GetUtcNow().UtcDateTime.AddDays(10),
            Status = GuestCheckInSessionStatus.Completo,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new StayWorld(booking, token);
    }

    /// <summary>A record of the stay with an intent on the connected account, paid online for <paramref name="collected"/> when above 0.</summary>
    private static async Task<StayTouristTax> SeedRecordAsync(AppDbContext db, Booking booking, string intentId, decimal collected = 0m)
    {
        var record = new StayTouristTax
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            Status = TouristTaxQuoteStatus.Calculated,
            Nights = 3,
            RetainUntil = StayTouristTax.RetentionFor(DateOnly.FromDateTime(booking.CheckOutDate)),
            Currency = StayCurrency.Euro,
            StripePaymentIntentId = intentId,
            StripeAccountId = Account,
            CollectedAmount = collected,
            CollectedAt = collected > 0m ? CheckIn.AddDays(-10) : null,
            CollectionStatus = collected > 0m ? TouristTaxCollectionStatus.PaidOnline : TouristTaxCollectionStatus.ToCollect,
        };
        db.StayTouristTaxes.Add(record);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return record;
    }

    /// <summary>The record of the stay with no intent: confirmed by the host for <paramref name="confirmedAmount"/> when given, with the collector the host declared when given.</summary>
    private static async Task<StayTouristTax> SeedStayRecordAsync(
        AppDbContext db,
        Booking booking,
        decimal? confirmedAmount = null,
        TouristTaxCollector? declaredCollector = null,
        bool paidAtProperty = false)
    {
        var record = new StayTouristTax
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            Status = TouristTaxQuoteStatus.Calculated,
            Nights = 3,
            RetainUntil = StayTouristTax.RetentionFor(DateOnly.FromDateTime(booking.CheckOutDate)),
            Currency = StayCurrency.Euro,
            DeclaredCollector = declaredCollector,
            CollectionStatus = paidAtProperty ? TouristTaxCollectionStatus.PaidAtProperty : TouristTaxCollectionStatus.ToCollect,
            CollectedAt = paidAtProperty ? Clock.GetUtcNow().UtcDateTime : null,
        };
        if (confirmedAmount is { } amount)
        {
            record.TaxableNights = 3;
            record.Total = amount;
            record.AmountDue = amount;
            record.Collector = TouristTaxCollector.Host;
            record.CalculationHash = new string('a', 64);
            record.ConfirmedAt = Clock.GetUtcNow().UtcDateTime;
            record.ConfirmedByUserId = "auth0|host";
        }

        db.StayTouristTaxes.Add(record);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return record;
    }

    private static async Task<StayTouristTax> ReloadAsync(AppDbContext db, Guid recordId)
    {
        db.ChangeTracker.Clear();
        return await db.StayTouristTaxes.AsNoTracking().SingleAsync(t => t.Id == recordId);
    }

    private static TestRig Rig(AppDbContext db, params (Guid BookingId, StayTouristTaxView View)[] calculations) =>
        RigWith(db, new RigOptions(), calculations);

    /// <summary>What a test changes in the rig: the flag, the cantonal rules and the channels whose portal collects the tax of a Swiss stay.</summary>
    private sealed record RigOptions(
        bool FlagOn = true,
        SwissTouristTaxOptions? Swiss = null,
        IReadOnlyList<BookingSource>? PlatformCollected = null,
        bool PublishableKeyIsSet = true);

    private static TestRig RigWith(AppDbContext db, RigOptions options, params (Guid BookingId, StayTouristTaxView View)[] calculations)
    {
        var tax = new Mock<IStayTouristTaxService>();
        foreach (var (bookingId, view) in calculations)
            tax.Setup(t => t.GetAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(view);

        var stripe = new Mock<IStripeService>();
        var pushes = new RecordingPushQueue();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:PublishableKey"] = options.PublishableKeyIsSet ? PublishableKey : null })
            .Build();
        var lines = new Mock<IGuestDocumentLineBuilder>();
        lines
            .Setup(l => l.PlatformCollectedChannelsOf(It.IsAny<OrgFiscalIssuerProfile>()))
            .Returns(options.PlatformCollected ?? []);
        var service = new TouristTaxCollectionService(
            db,
            tax.Object,
            stripe.Object,
            options.FlagOn ? new TestFeatureFlags(FeatureFlags.TouristTaxOnlinePayment) : new TestFeatureFlags(),
            configuration,
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock),
            new KeyTexts(),
            lines.Object,
            Microsoft.Extensions.Options.Options.Create(options.Swiss ?? new SwissTouristTaxOptions()),
            NullLogger<TouristTaxCollectionService>.Instance,
            pushes,
            Clock);
        return new TestRig(service, stripe, pushes);
    }

    private static TouristTaxIntentEvent IntentEvent(
        string intentId,
        string? account,
        Guid bookingId,
        Guid recordId,
        long amountMinor,
        DateTime? at = null)
    {
        var metadata = new Dictionary<string, string>
        {
            [TouristTaxCharges.KindMetadataKey] = TouristTaxCharges.Kind,
            [TouristTaxCharges.BookingMetadataKey] = bookingId.ToString(),
            [TouristTaxCharges.RecordMetadataKey] = recordId.ToString(),
        };
        return new TouristTaxIntentEvent(intentId, account, metadata, amountMinor, "eur", at);
    }

    private static Event SucceededEvent(string eventId, string intentId, Dictionary<string, string> metadata, long amountMinor) => new()
    {
        Id = eventId,
        Type = "payment_intent.succeeded",
        Account = Account,
        Data = new EventData
        {
            Object = new PaymentIntent
            {
                Id = intentId,
                Amount = amountMinor,
                AmountReceived = amountMinor,
                Currency = "eur",
                Status = "succeeded",
                Metadata = metadata,
            },
        },
    };

    private static Event RefundedEvent(string eventId, string intentId, long refundedMinor) => new()
    {
        Id = eventId,
        Type = "charge.refunded",
        Account = Account,
        Data = new EventData
        {
            Object = new Charge
            {
                Id = $"ch_{eventId}",
                PaymentIntentId = intentId,
                AmountRefunded = refundedMinor,
            },
        },
    };

    private static StripeWebhookHandler WebhookHandler(AppDbContext db, ITouristTaxCollectionService collection, IPaymentRefundService? refunds = null) =>
        new(
            new PaymentRepository(db),
            new BookingRepository(db),
            Mock.Of<IConnectOnboardingService>(),
            db,
            Mock.Of<IStripeBillingService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IPlatformInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            refunds ?? Mock.Of<IPaymentRefundService>(),
            TestCheckoutPaymentSettlement.Create(db),
            TestDeferredCharges.Create(db),
            Mock.Of<ISupplierPaymentWebhookService>(),
            NullLogger<StripeWebhookHandler>.Instance,
            touristTaxCollection: collection);

    private static PublicGuestCheckInController PublicController(ITouristTaxCollectionService collection) => new(
        Mock.Of<IGuestCheckInService>(),
        Mock.Of<IAlloggiatiCodeTableService>(),
        Mock.Of<ICheckInDocumentReader>(),
        Mock.Of<IAlloggiatiReportScheduler>(),
        Mock.Of<IIdentityVerificationFlowService>(),
        new TestFeatureFlags(FeatureFlags.TouristTaxOnlinePayment),
        Mock.Of<IStringLocalizer<SharedResources>>(),
        NullLogger<PublicGuestCheckInController>.Instance,
        collection)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    private sealed record StayWorld(Booking Booking, string Token);

    private sealed record TestRig(TouristTaxCollectionService Service, Mock<IStripeService> Stripe, RecordingPushQueue Pushes);

    /// <summary>The key itself: the tests read which sentence was chosen, not its words.</summary>
    private sealed class KeyTexts : IDocumentTexts
    {
        public string Get(string key, string language, params object[] args) => key;
    }
}
