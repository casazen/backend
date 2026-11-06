using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Host changes to an existing booking (PC-07, A2-07, A2-08). See <see cref="IHostBookingService"/>.
/// </summary>
/// <remarks>
/// <para>Every change takes the iCal-sync lock of the property before the advisory lock of the booking used by the cancellation
/// (BK-02), so a feed sync cannot add an OTA block between the block check and the commit, and a change and a
/// cancellation sent together never overwrite each other. New dates (and a confirmation) are saved through
/// <see cref="IBookingRepository.UpdateAsync"/>, which adds the lock of the property and checks, in the same transaction,
/// that no other booking takes them. Only the columns that changed are written.</para>
/// <para><b>The money of a booking of the booking site</b> (E08/F02/US01/T01, NF-75). The edit and the money are one decision, in one transaction:
/// the checks come first (the booking, the capacity, the dates free for the other bookings and the calendar blocks), then the Stripe call (the
/// refund, or the charge of the saved card), then the writes (the booking, its event, the refund or the payment row, the payment link), then the
/// commit. When Stripe refuses, nothing was written. Its idempotency key (<see cref="BookingEditSettlementRules.RefundKey"/>) is made from the
/// <c>UpdatedAt</c> the booking has until the commit, so a request repeated after an answer was lost gets the refund Stripe already made and a
/// request repeated after the commit finds the price paid and moves nothing: the guest is never refunded twice. The guest is told by email after
/// the commit (<see cref="BookingNotifier.BookingModifiedAsync"/>); a failed email is logged and never undoes the edit.</para>
/// </remarks>
public sealed class HostBookingService(
    AppDbContext db,
    IBookingService bookingService,
    IBookingRepository bookingRepository,
    ICheckoutHoldExpiryService checkoutHoldExpiry,
    PropertyICalSyncService propertyICalSyncService,
    IOnSiteBookingRequestService onSiteRequests,
    ILogger<HostBookingService> logger,
    TimeProvider? timeProvider = null,
    IDomainEventRecorder? domainEvents = null,
    OtaChannelActionPolicy? channelPolicy = null,
    IPaymentRefundService? refundService = null,
    DeferredChargeService? savedCardCharges = null,
    IBookingPaymentLinkService? paymentLinks = null,
    BookingNotifier? notifier = null) : IHostBookingService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Which channels let the host change a reservation from CasaZen: closed for every one unless the configuration opens it (D8).</summary>
    private readonly OtaChannelActionPolicy _channelPolicy = channelPolicy ?? OtaChannelActionPolicy.Closed;

    public async Task<HostBookingUpdateResult> UpdateAsync(HostBookingUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var (checkIn, checkOut) = ValidatedStay(update);
        var current = await ReadCurrentAsync(update, cancellationToken);

        // Abandoned checkout holds of the new dates are released first, as for a new booking (BK-21). Outside the
        // booking lock: the expiry takes its own locks and may call Stripe.
        if (checkIn != current.CheckInDate.Date || checkOut != current.CheckOutDate.Date)
            await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(current.PropertyId, checkIn, checkOut, cancellationToken);

        await using var transaction = await LockBookingAsync(current.PropertyId, update.BookingId, cancellationToken);
        var booking = await LoadAsync(update.BookingId, cancellationToken);
        var edit = await EvaluateAsync(booking, update, checkIn, checkOut, cancellationToken);

        // Money moves only for who may handle it (payment.write). The plan is known now, under the lock: before any date check, any call to Stripe and any
        // write, and whatever the plan was when the caller was first asked (the controller worked it out too, but a payment may have come in since).
        if (edit.Plan.Action != BookingSettlementAction.None && !update.CanMoveMoney)
        {
            logger.LogWarning("Booking {BookingId} not changed: the caller may not handle the money of the edit ({Action})", booking.Id, edit.Plan.Action);
            throw new DomainForbiddenException(BookingErrorCodes.EditPaymentPermissionRequired, "BookingEditPaymentPermissionRequired");
        }

        // The checks that can refuse the dates come before any money moves.
        if (edit.DatesChanged)
        {
            // The dates lock of the property first (the repository takes it again when it saves): a manual block is
            // written under it (PC-09), so it cannot take the new nights between this check and the save.
            await BookingRepository.LockPropertyDatesAsync(db, booking.PropertyId, cancellationToken);
            await EnsureDatesFreeAsync(booking, checkIn, checkOut, cancellationToken);
        }

        // The Stripe call, before anything is written. A refusal throws and nothing was saved.
        var money = await SettleBeforeSaveAsync(booking, edit, update.RequestedByUserId, cancellationToken);

        if (edit.Price is { } price)
            Reprice(booking, update, edit, price);

        if (edit.DatesChanged)
        {
            booking.CheckInDate = checkIn;
            booking.CheckOutDate = checkOut;

            // The free cancellation deadline (and, for "Paga alla scadenza", the day the card is charged) follows the new check-in.
            if (booking.Source == BookingSource.Direct && booking.FreeRefundDeadline is not null)
                booking.FreeRefundDeadline = edit.Price!.FreeRefundDeadline;
        }

        booking.SpecialRequests = update.SpecialRequests?.Trim() ?? string.Empty;
        booking.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        if (money.Charge is { } charge)
            await RecordChargeAsync(booking, charge, cancellationToken);

        // Saved by the same save as the change; a change the repository refuses takes its event back (ADR-022-R1).
        await BookingEvents.RecordAsync(domainEvents, db, DomainEventType.BookingModified, booking, BookingEventOrigin.Host, cancellationToken);
        await SaveAsync(booking, checkOverlap: edit.DatesChanged, cancellationToken);

        // The payment link needs the new total saved: it is made in the same transaction, so a link Stripe cannot make takes the edit back.
        var settlement = await LinkAfterSaveAsync(booking, edit, money.Settlement, cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Booking {BookingId} updated by the host (dates changed: {DatesChanged}, guests changed: {GuestsChanged}, money: {Action} {State})",
            booking.Id,
            edit.DatesChanged,
            edit.GuestsChanged,
            settlement.Action,
            settlement.State);

        await TellTheGuestAsync(booking, edit, settlement, cancellationToken);
        return new HostBookingUpdateResult(booking, settlement);
    }

    public async Task<BookingEditQuote> QuoteEditAsync(HostBookingUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var (checkIn, checkOut) = ValidatedStay(update);
        await ReadCurrentAsync(update, cancellationToken);

        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == update.BookingId, cancellationToken)
            ?? throw BookingNotFound(update.BookingId);
        var edit = await EvaluateAsync(booking, update, checkIn, checkOut, cancellationToken);

        // The same refusals as the edit, read without a lock: the answer is what the edit would do now.
        if (edit.DatesChanged)
            await EnsureDatesFreeAsync(booking, checkIn, checkOut, cancellationToken);

        return edit.Quote;
    }

    public async Task<Booking> ConfirmAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var current = await db.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.PropertyId, b.CheckInDate, b.CheckOutDate, b.Source, b.PaymentOption })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BookingNotFound(bookingId);

        // One confirmation for every pending booking: a "pay at the property" request is the host's acceptance of
        // decision D5, with its own rules (guest email confirmed, deadline, OTA blocks) and emails (BK-06).
        if (current.Source == BookingSource.Direct && current.PaymentOption == PaymentOption.OnSite)
            return await onSiteRequests.AcceptAsync(bookingId, cancellationToken);

        await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(
            current.PropertyId, current.CheckInDate, current.CheckOutDate, cancellationToken);

        await using var transaction = await LockBookingAsync(current.PropertyId, bookingId, cancellationToken);
        var booking = await LoadAsync(bookingId, cancellationToken);

        if (booking.Status != BookingStatus.Pending)
            throw new DomainConflictException(BookingErrorCodes.NotPending, "BookingNotPending");
        if (booking.Source != BookingSource.Manual)
            throw new DomainRuleException(BookingErrorCodes.NotConfirmable, "BookingNotConfirmable");

        // A pending booking may have been overtaken meanwhile by another booking (checked by the repository under the
        // property lock) or by a block imported from a channel.
        if (await propertyICalSyncService.HasOverlappingBlockAsync(
                booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, cancellationToken))
            throw DatesUnavailable();

        booking.Status = BookingStatus.Confirmed;
        booking.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        booking.ConfirmedAt = booking.UpdatedAt;

        // The repository also issues the guest check-in token of a confirmed booking.
        await BookingEvents.RecordAsync(domainEvents, db, DomainEventType.BookingConfirmed, booking, BookingEventOrigin.Host, cancellationToken);
        await SaveAsync(booking, checkOverlap: true, cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Manual booking {BookingId} confirmed by the host", booking.Id);
        return booking;
    }

    /// <summary>The days and the guests of an edit, validated: a stay of 1 to the maximum of nights, at least one adult.</summary>
    private static (DateTime CheckIn, DateTime CheckOut) ValidatedStay(HostBookingUpdate update)
    {
        var checkIn = update.CheckInDate.Date;
        var checkOut = update.CheckOutDate.Date;
        if (checkOut <= checkIn || (checkOut - checkIn).Days > TouristTaxCalculator.MaxNights)
            throw new DomainRuleException(BookingErrorCodes.InvalidDates, "BookingInvalidDates");
        if (update.NumberOfGuests < 1 || update.NumberOfChildren < 0 || update.NumberOfChildren >= update.NumberOfGuests)
            throw new DomainRuleException(BookingErrorCodes.GuestsInvalid, "BookingGuestsInvalid");

        return (checkIn, checkOut);
    }

    /// <summary>
    /// The booking as it is now (through the tenant filter: another org's booking is not found), and the refusals that come before anything else is
    /// done: the amounts of the channel are never edited (ADR-033-R3), and a booking of a channel is changed on its channel (E08/F02/US02/T01, D8,
    /// NF-75), where the channel lets the host do it from CasaZen and nowhere else, so the booking never differs from what the channel holds.
    /// </summary>
    private async Task<CurrentBooking> ReadCurrentAsync(HostBookingUpdate update, CancellationToken cancellationToken)
    {
        var current = await db.Bookings.AsNoTracking()
            .Where(b => b.Id == update.BookingId)
            .Select(b => new CurrentBooking(
                b.PropertyId,
                b.Source,
                b.OctorateReservationId,
                b.CheckInDate,
                b.CheckOutDate,
                b.OtaCommission,
                b.OtaNetAmount,
                b.OtaPaidAmount,
                b.OtaBalanceAmount))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BookingNotFound(update.BookingId);

        if (update.OtaAmounts is { } sentAmounts
            && sentAmounts.Edits(new OtaAmountsInput(current.OtaCommission, current.OtaNetAmount, current.OtaPaidAmount, current.OtaBalanceAmount)))
        {
            throw new DomainRuleException(BookingErrorCodes.OtaAmountsReadonly, "BookingOtaAmountsReadonly");
        }

        if (OtaChannelActionPolicy.IsChannelBooking(current.Source, current.OctorateReservationId) && !_channelPolicy.For(current.Source).CanModify)
            throw _channelPolicy.ChangeNotAllowed(current.Source);

        return current;
    }

    /// <summary>
    /// What an edit comes to for <paramref name="booking"/>: the rule of the booking (cancelled, started, from a channel), the price of the new stay
    /// (the same as a new booking of the host: nightly rate x nights + cleaning fee, tourist tax of BK-03) and, for a booking of the booking site, the
    /// money. Nothing is written, locked or sent.
    /// </summary>
    private async Task<EditEvaluation> EvaluateAsync(
        Booking booking,
        HostBookingUpdate update,
        DateTime checkIn,
        DateTime checkOut,
        CancellationToken cancellationToken)
    {
        // The rule of the `canEdit` of every booking answer (SR-05a): a cancelled booking changes nothing, not even its notes.
        var editability = BookingEditRules.Of(booking);
        if (editability.Reason == BookingEditLock.Cancelled)
            throw new DomainRuleException(BookingErrorCodes.NotEditable, "BookingNotEditable");

        // A "direct" booking that CasaZen did not make: the import maps the booking engine of Octorate (its own channel) to Direct, and such a
        // reservation has no mirror row (CasaZen writes nothing on a reservation that came from Octorate). An edit of its dates or guests would never
        // reach Octorate, the two calendars would differ, and the guest would be told of a change Octorate does not hold: they stay Octorate's to change.
        if (editability.Allowed
            && booking.Source == BookingSource.Direct
            && !string.IsNullOrWhiteSpace(booking.OctorateReservationId)
            && !await db.OctorateBookingSyncs.AsNoTracking().AnyAsync(s => s.BookingId == booking.Id, cancellationToken))
        {
            editability = BookingEditability.LockedBy(BookingEditLock.SourceLocked);
        }

        var datesChanged = checkIn != booking.CheckInDate.Date || checkOut != booking.CheckOutDate.Date;
        var (storedAdults, storedChildren) = BookingGuestCounts.Of(booking);
        var adults = update.NumberOfGuests - update.NumberOfChildren;
        var guestsChanged = adults != storedAdults || update.NumberOfChildren != storedChildren;

        DirectBookingQuote? price = null;
        if (datesChanged || guestsChanged)
        {
            if (editability.Reason == BookingEditLock.Started)
                throw new DomainRuleException(BookingErrorCodes.StayLocked, "BookingStayLocked");

            // A booking from a channel keeps the price, payment, free cancellation deadline and check-in link agreed there; one of the
            // booking site that is not confirmed yet is a request, not a booking. Only the bookings of the host and the confirmed ones of
            // the booking site are priced here.
            if (editability.Reason == BookingEditLock.SourceLocked)
                throw new DomainRuleException(BookingErrorCodes.SourceLocked, "BookingUpdateSourceLocked");

            if (checkIn != booking.CheckInDate.Date && checkIn < _clock.TodayInRome())
                throw new DomainRuleException(BookingErrorCodes.CheckInInPast, "BookingCheckInInPast");

            price = await bookingService.PriceHostStayAsync(
                booking.Property, checkIn, checkOut, adults, update.NumberOfChildren, update.ChildrenAges, cancellationToken);
            if (price.TouristTax.Status == TouristTaxQuoteStatus.ChildAgesRequired)
                throw new DomainRuleException(DirectBookingErrorCodes.ChildAgesRequired, "TouristTaxChildAgesRequired");

            // The tax of the new dates cannot be worked out (no rate for some night, a category or a night price missing) while the booking holds a tax
            // that the guest paid: the new price would leave it out and the money of the edit would give it back by itself. Nothing is changed.
            if (booking.Source == BookingSource.Direct
                && booking.TouristTax > 0m
                && price.TouristTax.Status != TouristTaxQuoteStatus.Calculated)
            {
                throw new DomainRuleException(BookingErrorCodes.TouristTaxUnavailable, "BookingTouristTaxUnavailable");
            }
        }

        var currency = StayCurrency.ForProperty(booking.Property);
        var money = await ReadMoneyAsync(booking, cancellationToken);
        var newTotal = price?.TotalPrice ?? booking.TotalPrice;

        // A payment that is not settled yet (Stripe is processing it, or the guest is asked to pay it) makes what was paid, and so the difference,
        // uncertain: the price of a booking of the booking site waits for it, or the money would be counted twice. For EVERY change of the price, not
        // only when the plan moves money: a booking "Paga alla scadenza" whose charge failed has paid nothing (the plan is `none`) and the PaymentIntent
        // the guest is asked to pay, which the retries confirm too, keeps the old amount.
        if (booking.Source == BookingSource.Direct && price is not null && (money.Processing || money.AwaitsGuestPayment))
            throw new DomainConflictException(BookingErrorCodes.EditPaymentInProgress, "BookingEditPaymentInProgress");

        // The money follows the price only for a confirmed booking of the booking site; the bookings of the host are settled by hand ("Registra incasso").
        var plan = booking.Source == BookingSource.Direct && price is not null
            ? BookingEditSettlementRules.Plan(newTotal, money.Paid, money.Refundable, money.CollectsOnline, HasSavedCard(booking))
            : BookingEditPlan.None;

        // A link that cannot be made is known before the host confirms: the answer is the same as the edit's.
        if (plan.Action == BookingSettlementAction.PaymentLink)
        {
            if (paymentLinks is null)
                throw new InvalidOperationException("The payment link service is not available.");

            await paymentLinks.EnsureAvailableAsync(booking.OrgId, cancellationToken);
        }

        var quote = new BookingEditQuote(
            currency,
            BookingEditSettlementRules.Cents(booking.TotalPrice),
            BookingEditSettlementRules.Cents(newTotal),
            BookingEditSettlementRules.Cents(money.Paid),
            BookingEditSettlementRules.Cents(newTotal - money.Paid),
            plan.Action,
            plan.Amount,
            plan.OfflineRefundDue);
        return new EditEvaluation(datesChanged, guestsChanged, adults, price, money, plan, quote);
    }

    /// <summary>
    /// What the guest has paid and not got back, and what can still go back online: the payments collected minus the refunds made or started (online
    /// or recorded by the host), the Stripe payments with what each can still return (newest first), and the state of the money that decides whether a
    /// higher price is collected now.
    /// </summary>
    private async Task<MoneyFacts> ReadMoneyAsync(Booking booking, CancellationToken cancellationToken)
    {
        var payments = await db.Payments.AsNoTracking().Where(p => p.BookingId == booking.Id).ToListAsync(cancellationToken);
        var paymentIds = payments.Select(p => p.Id).ToList();
        var refunds = paymentIds.Count == 0
            ? []
            : await db.PaymentRefunds.AsNoTracking().Where(r => paymentIds.Contains(r.PaymentId)).ToListAsync(cancellationToken);

        var paid = 0m;
        var shares = new List<(DateTime At, Guid Id, decimal Refundable)>();
        foreach (var payment in payments.Where(p => p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded))
        {
            var summary = PaymentRefundService.Summarize(payment, refunds.Where(r => r.PaymentId == payment.Id));
            paid += Math.Max(0m, payment.Amount - payment.RefundedAmount - summary.PendingRefundAmount);
            if (summary.RefundableOnline && summary.RefundableAmount > 0m)
                shares.Add((payment.ProcessedAt ?? payment.CreatedAt, payment.Id, summary.RefundableAmount));
        }

        // The guest paid online at some point (even if it was refunded since): a higher price is collected online. A booking paid at the property,
        // or whose card is charged at its deadline and was not yet, is collected as it was agreed: the balance only grows.
        var paidOnline = payments.Any(p =>
            PaymentRefundService.PaymentIntentIdOf(p) is not null
            && p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded);
        var deferredPayment = DeferredCharges.FindPayment(booking, payments);
        var awaitsGuest = booking.PaymentOption == PaymentOption.OnCancellationDeadline
                          && booking.DeferredChargeFailedAt is not null
                          && deferredPayment?.Status == PaymentStatus.Failed;

        return new MoneyFacts(
            paid,
            [.. shares.OrderByDescending(s => s.At).ThenBy(s => s.Id).Select(s => new BookingRefundableShare(s.Id, s.Refundable))],
            booking.PaymentOption != PaymentOption.OnSite && paidOnline,
            payments.Any(p => p.Status == PaymentStatus.Processing),
            awaitsGuest,
            deferredPayment?.StripeAccountId);
    }

    /// <summary>
    /// The Stripe call of the edit, before anything is written: the refund of the overpayment, payment by payment (newest first), or the charge of the
    /// card the booking saved. Stripe refusing, or an answer that is not a payment made, is <see cref="BookingErrorCodes.SettlementFailed"/> (422) and
    /// nothing was saved. The payment link is made after the save (<see cref="LinkAfterSaveAsync"/>).
    /// </summary>
    private async Task<SettlementBeforeSave> SettleBeforeSaveAsync(
        Booking booking,
        EditEvaluation edit,
        string? requestedByUserId,
        CancellationToken cancellationToken)
    {
        var currency = edit.Quote.Currency;
        var plan = edit.Plan;
        switch (plan.Action)
        {
            case BookingSettlementAction.Refund:
                return new SettlementBeforeSave(await RefundAsync(booking, plan, currency, requestedByUserId, cancellationToken), null);

            case BookingSettlementAction.ChargeSavedCard:
                return await ChargeAsync(booking, edit, currency, cancellationToken);

            default:
                return new SettlementBeforeSave(BookingEditSettlement.NotNeeded(currency) with { OfflineRefundDue = plan.OfflineRefundDue }, null);
        }
    }

    private async Task<BookingEditSettlement> RefundAsync(
        Booking booking,
        BookingEditPlan plan,
        string currency,
        string? requestedByUserId,
        CancellationToken cancellationToken)
    {
        if (refundService is null)
            throw new InvalidOperationException("The refund service is not available.");

        var state = BookingSettlementState.Done;
        foreach (var part in plan.Refunds)
        {
            try
            {
                var refund = await refundService.RefundAsync(
                    new PaymentRefundRequest(
                        part.PaymentId,
                        part.Amount,
                        "Booking edit",
                        requestedByUserId,
                        BookingEditSettlementRules.RefundKey(booking.Id, booking.UpdatedAt, part.PaymentId),
                        // The guest is told once, by the email of the edit after the commit: the refund sends no "refund confirmed" of its own now
                        // (and its notification is claimed when Stripe confirmed it at once); one still pending is confirmed by a later email.
                        NotifyGuest: false),
                    cancellationToken);
                if (refund.Status is PaymentRefundStatus.Failed or PaymentRefundStatus.Canceled)
                {
                    logger.LogWarning(
                        "Booking {BookingId} not changed: Stripe refused the refund of payment {PaymentId} ({Reason})",
                        booking.Id,
                        part.PaymentId,
                        refund.FailureReason);
                    throw SettlementFailed();
                }

                // Stripe has it and has not confirmed it yet (or a retry of the same key is under way): the refund row is saved with the edit.
                if (refund.Status != PaymentRefundStatus.Succeeded)
                    state = BookingSettlementState.Pending;
            }
            catch (DomainRuleException ex) when (ex.Code != BookingErrorCodes.SettlementFailed)
            {
                logger.LogWarning(
                    "Booking {BookingId} not changed: payment {PaymentId} cannot be refunded ({Code})", booking.Id, part.PaymentId, ex.Code);
                throw SettlementFailed();
            }
        }

        return new BookingEditSettlement(BookingSettlementAction.Refund, state, plan.Amount, plan.OfflineRefundDue, currency);
    }

    private async Task<SettlementBeforeSave> ChargeAsync(Booking booking, EditEvaluation edit, string currency, CancellationToken cancellationToken)
    {
        if (savedCardCharges is null)
            throw new InvalidOperationException("The charge of the saved card is not available.");

        var amount = edit.Plan.Amount;
        var account = edit.Money.DeferredAccountId
                      ?? await db.Orgs
                          .AsNoTracking()
                          .Where(o => o.Id == booking.OrgId)
                          .Select(o => o.StripeConnectedAccountId)
                          .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(account))
        {
            logger.LogWarning("Booking {BookingId} not changed: no connected account to charge the saved card on", booking.Id);
            throw SettlementFailed();
        }

        PaymentIntent intent;
        try
        {
            intent = await savedCardCharges.ChargeSavedCardAsync(
                booking, account, amount, BookingEditSettlementRules.ChargeKey(booking.Id, booking.UpdatedAt, amount), cancellationToken);
        }
        catch (StripeException ex)
        {
            // Authentication required, declined, Stripe unreachable: the guest was not charged, the edit is not saved.
            logger.LogWarning(
                "Booking {BookingId} not changed: Stripe refused the charge of the saved card ({Code})", booking.Id, ex.StripeError?.Code ?? "no code");
            throw SettlementFailed();
        }

        var status = DeferredCharges.StatusOf(intent.Status);
        if (status is not (PaymentStatus.Completed or PaymentStatus.Processing))
        {
            logger.LogWarning("Booking {BookingId} not changed: the charge of the saved card is {Status}", booking.Id, intent.Status);
            throw SettlementFailed();
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = amount,
            Status = status,
            Method = Core.Entities.PaymentMethod.CreditCard,
            TransactionId = intent.Id,
            StripePaymentIntentId = intent.Id,
            StripeAccountId = account,
            Description = BookingEditChargeDescription,
            ProcessedAt = status == PaymentStatus.Completed ? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var settlement = new BookingEditSettlement(
            BookingSettlementAction.ChargeSavedCard,
            status == PaymentStatus.Completed ? BookingSettlementState.Done : BookingSettlementState.Pending,
            amount,
            0m,
            currency);
        return new SettlementBeforeSave(settlement, payment);
    }

    /// <summary>The charge of the saved card, written with the edit; a payment that came in is announced to the consumers of the payment events (the Octorate handler records it).</summary>
    private async Task RecordChargeAsync(Booking booking, Payment payment, CancellationToken cancellationToken)
    {
        db.Payments.Add(payment);
        if (payment.Status == PaymentStatus.Completed)
            PaymentEvents.RecordSucceeded(domainEvents, db, payment, await PaymentEvents.CurrencyOfAsync(db, booking, cancellationToken));
    }

    /// <summary>
    /// A higher price and no card to charge: the guest gets a payment link for the difference, made after the new total is saved and inside the same
    /// transaction, so a link Stripe cannot make takes the edit back (422 <see cref="BookingErrorCodes.SettlementFailed"/>).
    /// </summary>
    private async Task<BookingEditSettlement> LinkAfterSaveAsync(
        Booking booking,
        EditEvaluation edit,
        BookingEditSettlement settled,
        CancellationToken cancellationToken)
    {
        if (edit.Plan.Action != BookingSettlementAction.PaymentLink)
            return settled;

        try
        {
            var link = await paymentLinks!.CreateAsync(booking.Id, cancellationToken);
            return new BookingEditSettlement(BookingSettlementAction.PaymentLink, BookingSettlementState.Done, link.Amount, 0m, link.Currency);
        }
        catch (Exception ex) when (ex is PaymentProcessingException or StripeException or DomainRuleException)
        {
            logger.LogWarning("Booking {BookingId} not changed: the payment link of the difference could not be made ({Error})", booking.Id, ex.GetType().Name);
            throw SettlementFailed();
        }
    }

    /// <summary>The new price and guests of the booking (SR-06a: a price agreed for the old stay does not follow the new one).</summary>
    private static void Reprice(Booking booking, HostBookingUpdate update, EditEvaluation edit, DirectBookingQuote price)
    {
        booking.BasePrice = price.BasePrice;
        booking.CleaningFee = price.CleaningFee;
        booking.TouristTax = price.TouristTax.AmountOrZero;
        booking.TouristTaxAmount = price.TouristTax.AmountOrZero;
        booking.TotalPrice = price.TotalPrice;
        // The stay is priced anew (SR-06a): a price the host had agreed for the old dates and guests does not follow it.
        booking.PriceOverridden = false;
        booking.PriceOverrideNote = null;
        booking.NumberOfGuests = update.NumberOfGuests;
        booking.NumberOfAdults = edit.Adults;
        booking.NumberOfChildren = update.NumberOfChildren;
    }

    /// <summary>The guest is told after the commit: dates, guests, the new total and what happens to the money. A failure is logged and never undoes the edit.</summary>
    private async Task TellTheGuestAsync(
        Booking booking,
        EditEvaluation edit,
        BookingEditSettlement settlement,
        CancellationToken cancellationToken)
    {
        if (notifier is null || booking.Source != BookingSource.Direct || !(edit.DatesChanged || edit.GuestsChanged))
            return;

        try
        {
            await notifier.BookingModifiedAsync(booking.Id, settlement, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Email of the change of booking {BookingId} could not be queued", booking.Id);
        }
    }

    private async Task EnsureDatesFreeAsync(Booking booking, DateTime checkIn, DateTime checkOut, CancellationToken cancellationToken)
    {
        if (await propertyICalSyncService.HasOverlappingBlockAsync(booking.PropertyId, checkIn, checkOut, cancellationToken))
            throw DatesUnavailable();

        // The repository checks the other bookings again when it saves, under the same lock; read here, so no money moves for dates that are taken.
        if (await BookingRepository.TakesNightsOfAnotherBookingAsync(db, booking.Id, booking.PropertyId, checkIn, checkOut, cancellationToken))
            throw DatesUnavailable();
    }

    private static bool HasSavedCard(Booking booking) =>
        !string.IsNullOrWhiteSpace(booking.StripeCustomerId) && !string.IsNullOrWhiteSpace(booking.StripePaymentMethodId);

    private async Task SaveAsync(Booking booking, bool checkOverlap, CancellationToken cancellationToken)
    {
        if (checkOverlap)
        {
            try
            {
                // Joins the transaction: property lock + overlap check against every other booking not cancelled.
                await bookingRepository.UpdateAsync(booking);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Property not available", StringComparison.OrdinalIgnoreCase))
            {
                throw DatesUnavailable();
            }
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private Task<IDbContextTransaction?> LockBookingAsync(
        Guid propertyId,
        Guid bookingId,
        CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.PropertyICalSync, propertyId.ToString()),
            (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.ToString("N")));

    /// <summary>
    /// The booking as committed now, under the lock. The context may already track it (the controller read it for the
    /// authorization, before the lock): a query does not refresh a tracked entity, so it is reloaded, otherwise a change
    /// racing a cancellation would still see the old status and write over it.
    /// </summary>
    private async Task<Booking> LoadAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw BookingNotFound(bookingId);
        await db.Entry(booking).ReloadAsync(cancellationToken);
        return booking;
    }

    private static NotFoundException BookingNotFound(Guid bookingId) =>
        new($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    private static DomainConflictException DatesUnavailable() =>
        new(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");

    private static DomainRuleException SettlementFailed() =>
        new(BookingErrorCodes.SettlementFailed, "BookingSettlementFailed");

    /// <summary>The description of the payment row of the charge of the saved card (no personal data).</summary>
    internal const string BookingEditChargeDescription = "Price change of the booking";

    /// <summary>What the edit reads of the booking before anything else: where it is, where it came from and the amounts of the channel.</summary>
    private sealed record CurrentBooking(
        Guid PropertyId,
        BookingSource Source,
        string? OctorateReservationId,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        decimal? OtaCommission,
        decimal? OtaNetAmount,
        decimal? OtaPaidAmount,
        decimal? OtaBalanceAmount);

    /// <summary>The money of a booking: what was paid and not got back, the Stripe payments that can return it, and the state that decides the collection.</summary>
    private sealed record MoneyFacts(
        decimal Paid,
        IReadOnlyList<BookingRefundableShare> Refundable,
        bool CollectsOnline,
        bool Processing,
        bool AwaitsGuestPayment,
        string? DeferredAccountId);

    private sealed record EditEvaluation(
        bool DatesChanged,
        bool GuestsChanged,
        int Adults,
        DirectBookingQuote? Price,
        MoneyFacts Money,
        BookingEditPlan Plan,
        BookingEditQuote Quote);

    private sealed record SettlementBeforeSave(BookingEditSettlement Settlement, Payment? Charge);
}
