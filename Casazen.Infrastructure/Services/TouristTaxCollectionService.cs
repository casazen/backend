using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ITouristTaxCollectionService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>What is due</b> is <see cref="TouristTaxCollectionRules"/> on the stay as it is now: in Italy the calculation guest by guest of
/// <see cref="IStayTouristTaxService"/> (the confirmed record for a booking whose price included the tax), in Switzerland the rule of the canton
/// (<see cref="SwissTouristTaxOptions"/>, the invoicing plan's) when the host collects; who collects is <see cref="TouristTaxCollectorRules.Decide"/>
/// (Italy) or the channels the issuer's profile says collect it (Switzerland, <see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>).</item>
/// <item><b>Once, in order.</b> Every write of the collection of a booking runs under its payment lock (<c>BookingPayment</c>, the one of the payment link and
/// of «Registra incasso»): the amount, the intent and the record are decided one request at a time. One PaymentIntent per stay: the open one is kept while
/// its amount is the one due, else cancelled on Stripe before another is created; one being paid stops everything else until the webhook records it.</item>
/// <item><b>No <see cref="Payment"/> row</b>, ever: the collection is on <see cref="StayTouristTax"/>. Logs carry ids, codes and amounts in minor units, never
/// a guest's data.</item>
/// </list>
/// </remarks>
public sealed class TouristTaxCollectionService(
    AppDbContext db,
    IStayTouristTaxService stayTouristTax,
    IStripeService stripe,
    IFeatureFlags featureFlags,
    IConfiguration configuration,
    IInternalActivityService activities,
    IDocumentTexts texts,
    IGuestDocumentLineBuilder documentLines,
    IOptions<SwissTouristTaxOptions> swissTouristTax,
    ILogger<TouristTaxCollectionService> logger,
    IPushNotificationService? push = null,
    TimeProvider? timeProvider = null) : ITouristTaxCollectionService
{
    /// <summary>The reasons of the host's «To do» (<see cref="ActivityType.TouristTaxRefundDue"/>): keys of <c>SharedResources</c>, in the language of the org.</summary>
    internal const string RefundDifferenceReason = "TouristTaxTodoRefundDifference";
    internal const string PartialStayReason = "TouristTaxTodoPartialStay";
    internal const string RefundFailedReason = "TouristTaxTodoRefundFailed";

    /// <summary>PaymentIntent statuses the guest can still pay.</summary>
    private static readonly HashSet<string> PayableIntentStatuses = new(StringComparer.Ordinal)
    {
        "requires_payment_method",
        "requires_confirmation",
        "requires_action",
    };

    /// <summary>PaymentIntent statuses of money on its way or received: nothing else is done until the webhook records it.</summary>
    private static readonly HashSet<string> MovingIntentStatuses = new(StringComparer.Ordinal)
    {
        "processing",
        "requires_capture",
        "succeeded",
    };

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<TouristTaxGuestView?> GetForGuestAsync(string token, CancellationToken ct = default)
    {
        var booking = await BookingOfTokenAsync(token, ct);
        if (booking is null)
            return null;

        EnsureAvailable(booking.Org);
        var record = await RecordAsync(booking, tracked: false, ct);
        var (due, figures) = await EvaluateAsync(booking, record, null, ct);
        return new TouristTaxGuestView(
            due.Status,
            due.Reason,
            due.OnlinePayable,
            figures.AmountDue,
            Math.Max(0m, (record?.CollectedAmount ?? 0m) - (record?.RefundedAmount ?? 0m)),
            figures.Currency,
            figures.Lines,
            booking.Org.TouristTaxRequiredBeforeAccessCode);
    }

    public async Task<TouristTaxPaymentSession?> CreatePaymentIntentAsync(string token, CancellationToken ct = default)
    {
        var booking = await BookingOfTokenAsync(token, ct);
        if (booking is null)
            return null;

        EnsureAvailable(booking.Org);
        var publishableKey = configuration["Stripe:PublishableKey"];
        if (string.IsNullOrWhiteSpace(publishableKey))
            throw NotAvailable();

        var account = booking.Org.StripeConnectedAccountId!;
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.BookingPayment, booking.Id.ToString("N")));

        var record = await RecordAsync(booking, tracked: true, ct);
        if (record is not null)
            await db.Entry(record).ReloadAsync(ct);

        var (due, figures) = await EvaluateAsync(booking, record, null, ct);
        if (due.OnlinePayable <= 0m)
            throw new DomainConflictException(TouristTaxCharges.NothingToPayCode, "TouristTaxNothingToPay");

        var amountMinor = TouristTaxCollectionRules.ToMinorUnits(due.OnlinePayable);
        var now = UtcNow();
        if (record is null)
        {
            record = NewRecord(booking, figures, now);
            db.StayTouristTaxes.Add(record);
        }

        var intent = await KeepOpenIntentAsync(record, account, amountMinor, figures.Currency, ct);
        if (intent is null)
        {
            // Per stay and amount, and per the intent it replaces: a retry of the same request gets the same intent, an amount that comes back after
            // another one gets a new intent (the old one was cancelled).
            var idempotencyKey = $"tourist-tax:{record.Id:N}:{amountMinor}:{record.StripePaymentIntentId ?? "first"}";
            intent = await stripe.CreateConnectedAccountPaymentIntentAsync(
                account,
                amountMinor,
                StayCurrency.ForStripe(figures.Currency),
                MetadataOf(booking, record),
                idempotencyKey,
                TouristTaxCharges.PaymentDescription,
                ct);
            record.StripePaymentIntentId = intent.Id;
            record.StripeAccountId = account;
        }

        record.Currency = figures.Currency;
        record.CollectionStatus = due.Status;
        record.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Tourist tax of booking {BookingId}: payment intent {PaymentIntentId} of {AmountMinor} minor units ({Currency}) ready on {AccountId}",
            booking.Id,
            intent.Id,
            amountMinor,
            figures.Currency,
            account);
        return new TouristTaxPaymentSession(intent.Id, intent.ClientSecret, publishableKey, account, due.OnlinePayable, figures.Currency);
    }

    public async Task<TouristTaxCollectionView> GetForHostAsync(Guid bookingId, StayTouristTaxView? tax, CancellationToken ct = default)
    {
        var booking = await LoadBookingAsync(bookingId, ct) ?? throw BookingNotFound();
        var record = await RecordAsync(booking, tracked: false, ct);
        var (due, figures) = await EvaluateAsync(booking, record, tax, ct);
        return ViewOf(booking, record, due, figures);
    }

    public async Task<TouristTaxCollectionView> RecordPaidAtPropertyAsync(Guid bookingId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var booking = await LoadBookingAsync(bookingId, ct) ?? throw BookingNotFound();

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.BookingPayment, booking.Id.ToString("N")));

        var record = await RecordAsync(booking, tracked: true, ct);
        if (record is not null)
            await db.Entry(record).ReloadAsync(ct);

        var (due, figures) = await EvaluateAsync(booking, record, null, ct);
        if (record is { CollectionStatus: TouristTaxCollectionStatus.PaidAtProperty })
            return ViewOf(booking, record, due, figures);

        if (!AlloggiatiStatusRules.IsDue(booking) || figures.Collector == TouristTaxCollector.Channel)
            throw new DomainRuleException(TouristTaxCharges.NotApplicableCode, "TouristTaxCollectionNotApplicable");
        if (record is { CollectedAmount: > 0m })
            throw new DomainConflictException(TouristTaxCharges.PaidOnlineCode, "TouristTaxAlreadyPaidOnline");

        var now = UtcNow();
        if (record is null)
        {
            record = NewRecord(booking, figures, now);
            db.StayTouristTaxes.Add(record);
        }
        else if (record.StripePaymentIntentId is { } openIntentId)
        {
            // What the host collected can no longer be paid online too (never twice); a payment on its way is waited for.
            var account = record.StripeAccountId ?? booking.Org.StripeConnectedAccountId;
            var intent = await stripe.GetPaymentIntentAsync(openIntentId, account, ct);
            if (intent.Status is { } moving && MovingIntentStatuses.Contains(moving))
                throw PaymentInProgress();
            if (!string.Equals(intent.Status, "canceled", StringComparison.Ordinal))
                await stripe.CancelPaymentIntentAsync(openIntentId, account, $"tourist-tax-at-property:{record.Id:N}:{openIntentId}", ct);
        }

        record.CollectionStatus = TouristTaxCollectionStatus.PaidAtProperty;
        record.CollectedAt = now;
        record.Currency ??= figures.Currency;
        record.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation("Tourist tax of booking {BookingId} recorded as paid at the property by {UserId}", booking.Id, userId);
        var (after, afterFigures) = await EvaluateAsync(booking, record, null, ct);
        return ViewOf(booking, record, after, afterFigures);
    }

    public async Task<TouristTaxSettingsView?> GetSettingsAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, ct);
        return org is null ? null : SettingsOf(org);
    }

    public async Task<TouristTaxSettingsView?> SaveSettingsAsync(
        Guid orgId,
        bool enabled,
        bool requiredBeforeAccessCode,
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, ct);
        if (org is null)
            return null;

        org.TouristTaxOnlinePaymentEnabled = enabled;

        // Asked before the lock code only where the payment is offered: off, the code is never held back by the tax.
        org.TouristTaxRequiredBeforeAccessCode = enabled && requiredBeforeAccessCode;
        org.UpdatedAt = UtcNow();
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Tourist tax online payment of org {OrgId} set by {UserId}: enabled {Enabled}, before the lock code {RequiredBeforeAccessCode}",
            orgId,
            userId,
            org.TouristTaxOnlinePaymentEnabled,
            org.TouristTaxRequiredBeforeAccessCode);
        return SettingsOf(org);
    }

    public async Task<TouristTaxPaidNotice?> ApplyPaymentSucceededAsync(TouristTaxIntentEvent intentEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intentEvent);

        if (!TryGetId(intentEvent.Metadata, TouristTaxCharges.RecordMetadataKey, out var recordId)
            || !TryGetId(intentEvent.Metadata, TouristTaxCharges.BookingMetadataKey, out var bookingId))
        {
            logger.LogWarning(
                "Tourist tax payment intent {PaymentIntentId} names no stay in its metadata: nothing is recorded", intentEvent.PaymentIntentId);
            return null;
        }

        // The payment lock of the booking, joined to the transaction of the event: a host recording the payment at the property waits for it.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.BookingPayment, bookingId.ToString("N")));

        var record = await db.StayTouristTaxes
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(t => t.Id == recordId && t.BookingId == bookingId, ct);
        if (record is null)
        {
            logger.LogWarning(
                "Tourist tax payment intent {PaymentIntentId}: the stay record {RecordId} of booking {BookingId} does not exist, nothing is recorded",
                intentEvent.PaymentIntentId,
                recordId,
                bookingId);
            return null;
        }

        await db.Entry(record).ReloadAsync(ct);

        // Only the intent the stay asked for, on the account it was created on: an intent of another account carrying the same markers is not its payment.
        if (!string.Equals(record.StripePaymentIntentId, intentEvent.PaymentIntentId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(intentEvent.AccountId)
            || !string.Equals(record.StripeAccountId, intentEvent.AccountId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Tourist tax payment intent {PaymentIntentId} (account {AccountId}) is not the intent of stay record {RecordId}: nothing is recorded",
                intentEvent.PaymentIntentId,
                intentEvent.AccountId ?? "none",
                record.Id);
            return null;
        }

        // Once: the same payment delivered again (or by the other endpoint) changes nothing.
        if (record.CollectedAmount > 0m)
            return null;

        var now = UtcNow();
        record.CollectedAmount = TouristTaxCollectionRules.FromMinorUnits(intentEvent.AmountReceivedMinor);
        record.Currency = StayCurrency.Normalize(intentEvent.Currency);
        record.CollectedAt ??= intentEvent.OccurredAtUtc ?? now;
        if (record.CollectionStatus != TouristTaxCollectionStatus.PaidAtProperty)
            record.CollectionStatus = TouristTaxCollectionStatus.PaidOnline;
        record.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var language = await db.Orgs.AsNoTracking().Where(o => o.Id == record.OrgId).Select(o => o.DefaultLanguage).FirstOrDefaultAsync(ct);
        logger.LogInformation(
            "Tourist tax of booking {BookingId} paid online: {AmountMinor} minor units in {Currency} (intent {PaymentIntentId})",
            record.BookingId,
            intentEvent.AmountReceivedMinor,
            record.Currency,
            intentEvent.PaymentIntentId);
        return new TouristTaxPaidNotice(record.Id, record.BookingId, record.OrgId, intentEvent.PaymentIntentId, DocumentLanguages.Normalize(language));
    }

    public async Task<bool> ApplyChargeRefundedAsync(TouristTaxChargeRefund refund, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refund);
        if (string.IsNullOrWhiteSpace(refund.PaymentIntentId))
            return false;

        var record = await db.StayTouristTaxes
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(t => t.StripePaymentIntentId == refund.PaymentIntentId, ct);
        if (record is null)
            return false;

        if (!string.Equals(record.StripeAccountId, refund.AccountId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Refund of tourist tax intent {PaymentIntentId} reported by account {AccountId}, not the stay's: nothing is recorded",
                refund.PaymentIntentId,
                refund.AccountId ?? "none");
            return true;
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.BookingPayment, record.BookingId.ToString("N")));
        await db.Entry(record).ReloadAsync(ct);

        // A refund of a payment not recorded yet: the success arrives later (events are not in order), the refund is delivered again after it.
        if (record.CollectedAmount <= 0m)
            throw new TouristTaxCollectionPendingException(record.BookingId);

        var refunded = Math.Min(record.CollectedAmount, TouristTaxCollectionRules.FromMinorUnits(refund.AmountRefundedMinor));
        if (refunded > record.RefundedAmount)
        {
            record.RefundedAmount = refunded;
            record.UpdatedAt = UtcNow();
            await db.SaveChangesAsync(ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Tourist tax of booking {BookingId}: {RefundedMinor} minor units refunded of intent {PaymentIntentId}",
            record.BookingId,
            refund.AmountRefundedMinor,
            refund.PaymentIntentId);
        return true;
    }

    public async Task CompletePaidAsync(TouristTaxPaidNotice notice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        TouristTaxLiveRefresh.Tell(push, texts, notice, logger);
        try
        {
            await ReconcileAsync(notice.BookingId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The payment is recorded: the difference owed back is evaluated again at the check-in, at the check-out and at a change of the booking.
            logger.LogError(
                "Tourist tax of booking {BookingId}: the collection could not be reconciled after the payment ({Error})", notice.BookingId, ex.GetType().Name);
        }
    }

    public async Task ApplyStayEndedAsync(Guid bookingId, DateTime endedAtUtc, CancellationToken ct = default)
    {
        var booking = await LoadBookingAsync(bookingId, ct);

        // Gone, or active again (the event is older than the booking): nothing to give back.
        if (booking is null || AlloggiatiStatusRules.IsDue(booking))
            return;

        string? todoReason = null;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, ct, (PostgresAdvisoryLocks.Scope.BookingPayment, booking.Id.ToString("N"))))
        {
            var record = await RecordAsync(booking, tracked: true, ct);
            if (record is null)
                return;

            await db.Entry(record).ReloadAsync(ct);

            // An intent nobody paid is cancelled: the guest of a stay that will not happen can no longer pay it. One being paid is waited for.
            if (record.StripePaymentIntentId is { } openIntentId && record.CollectedAmount <= 0m)
            {
                var intent = await stripe.GetPaymentIntentAsync(openIntentId, record.StripeAccountId, ct);
                if (intent.Status is { } moving && MovingIntentStatuses.Contains(moving))
                    throw new TouristTaxCollectionPendingException(booking.Id);
                if (!string.Equals(intent.Status, "canceled", StringComparison.Ordinal))
                    await stripe.CancelPaymentIntentAsync(openIntentId, record.StripeAccountId, $"tourist-tax-cancel:{record.Id:N}:{openIntentId}", ct);
            }

            var held = record.CollectedAmount - record.RefundedAmount;
            if (TouristTaxCollectionRules.EndedBeforeAnyNight(booking, endedAtUtc))
            {
                // No night spent: the tax is not due, and what was paid online goes back in full on the account it was paid on.
                if (held > 0m)
                    todoReason = await RefundAsync(record, held, ct);

                record.CollectionStatus = TouristTaxCollectionStatus.NotDue;
                record.UpdatedAt = UtcNow();
            }
            else if (held > 0m)
            {
                // Cancelled after the arrival: the nights spent are due, the rest is the host's decision.
                todoReason = PartialStayReason;
            }

            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }

        if (todoReason is not null)
            await RaiseRefundTodoAsync(booking, todoReason, ct);
    }

    public async Task ReconcileAsync(Guid bookingId, CancellationToken ct = default)
    {
        var booking = await LoadBookingAsync(bookingId, ct);
        if (booking is null)
            return;

        // Under the payment lock of the booking, from what is committed: a payment recorded meanwhile is never overwritten by an older state.
        TouristTaxCollectionDue due;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, ct, (PostgresAdvisoryLocks.Scope.BookingPayment, booking.Id.ToString("N"))))
        {
            var record = await RecordAsync(booking, tracked: true, ct);
            if (record is not null)
                await db.Entry(record).ReloadAsync(ct);

            (due, _) = await EvaluateAsync(booking, record, null, ct);
            if (record is not null && record.CollectionStatus != due.Status)
            {
                record.CollectionStatus = due.Status;
                record.UpdatedAt = UtcNow();
                await db.SaveChangesAsync(ct);
            }

            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }

        // A stay that is no longer active is the business of ApplyStayEndedAsync (the automatic refund, or the «To do» of a partial stay).
        if (due.RefundDue > 0m && AlloggiatiStatusRules.IsDue(booking))
            await RaiseRefundTodoAsync(booking, RefundDifferenceReason, ct);
    }

    public async Task ReconcileAfterChangeAsync(Guid bookingId, CancellationToken ct = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.TouristTaxOnlinePayment))
            return;

        try
        {
            await ReconcileAsync(bookingId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The host's change is saved: the difference owed back is evaluated again at the next event of the stay and shown at once in the host's block.
            logger.LogError(
                "Tourist tax of booking {BookingId}: the collection could not be reconciled after a change of the tax ({Error})", bookingId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The booking of a check-in link that can pay: the link is known, not expired, and its stay is confirmed or in progress (like the check-in page; a
    /// completed check-in still pays its tax). <c>null</c> otherwise. Read without the tenant filter, with the org of the link as an explicit predicate.
    /// </summary>
    private async Task<Booking?> BookingOfTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var tokenHash = GuestCheckInService.HashToken(token);
        var session = await db.GuestCheckInSessions
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => s.TokenHash == tokenHash)
            .Select(s => new { s.BookingId, s.OrgId, s.Status, s.ExpiresAt })
            .FirstOrDefaultAsync(ct);
        if (session is null || session.Status == GuestCheckInSessionStatus.Scaduto || session.ExpiresAt < UtcNow())
            return null;

        var booking = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Include(b => b.Property)
            .Include(b => b.Org)
            .FirstOrDefaultAsync(b => b.Id == session.BookingId && b.OrgId == session.OrgId, ct);

        return booking is { Status: BookingStatus.Confirmed or BookingStatus.CheckedIn } && booking.Property is not null && booking.Org is not null
            ? booking
            : null;
    }

    /// <summary>The booking with its property and its org, through the tenant filter of the caller (the host's org, the org of an event, none for a job).</summary>
    private Task<Booking?> LoadBookingAsync(Guid bookingId, CancellationToken ct) =>
        db.Bookings
            .AsNoTracking()
            .Include(b => b.Property)
            .Include(b => b.Org)
            .FirstOrDefaultAsync(b => b.Id == bookingId, ct);

    /// <summary>The record of the stay, with the org of the booking as an explicit predicate.</summary>
    private async Task<StayTouristTax?> RecordAsync(Booking booking, bool tracked, CancellationToken ct)
    {
        var query = db.StayTouristTaxes
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(t => t.BookingId == booking.Id && t.OrgId == booking.OrgId);
        return tracked ? await query.FirstOrDefaultAsync(ct) : await query.AsNoTracking().FirstOrDefaultAsync(ct);
    }

    /// <summary>What is due of the stay now and the figures it was decided from.</summary>
    private async Task<(TouristTaxCollectionDue Due, TaxFigures Figures)> EvaluateAsync(
        Booking booking,
        StayTouristTax? record,
        StayTouristTaxView? tax,
        CancellationToken ct)
    {
        var figures = FiscalDocumentRules.IsSwiss(booking.Property.Country)
            ? await SwissFiguresAsync(booking, ct)
            : await ItalianFiguresAsync(booking, record, tax, ct);
        figures = figures with { IncludedInBooking = await IncludedInBookingAsync(booking, ct) };

        var due = TouristTaxCollectionRules.Evaluate(new TouristTaxCollectionFacts(
            AlloggiatiStatusRules.IsDue(booking),
            TouristTaxCollectionRules.EndedBeforeAnyNight(booking, null),
            figures.Collector,
            figures.AmountDue,
            figures.ConfirmedAmountDue,
            figures.IncludedInBooking,
            record?.CollectionStatus ?? TouristTaxCollectionStatus.ToCollect,
            record?.CollectedAmount ?? 0m,
            record?.RefundedAmount ?? 0m));
        return (due, figures);
    }

    /// <summary>
    /// The tax CasaZen already collected with the price of the booking (decision O12: «per le dirette già pagate»): <c>Booking.TouristTaxAmount</c> of a
    /// <see cref="BookingSource.Direct"/> booking that has a payment through CasaZen that went through (completed, or completed and partly refunded); 0 for any
    /// other booking. The amount is written on every booking, so it proves nothing by itself: the quote of a host's manual booking, the figure of an OTA
    /// reservation, a direct booking paid at the property or at the deadline are no tax collected by CasaZen. For a channel stay the double charge is the
    /// business of <see cref="TouristTaxChannelRule"/> and of the host's declaration (<see cref="CollectorAsync"/>), decided before this amount counts.
    /// </summary>
    private async Task<decimal> IncludedInBookingAsync(Booking booking, CancellationToken ct)
    {
        var amount = Math.Max(0m, booking.TouristTaxAmount);
        if (amount <= 0m || booking.Source != BookingSource.Direct)
            return 0m;

        var paidThroughCasaZen = await db.Payments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                p => p.BookingId == booking.Id
                     && p.OrgId == booking.OrgId
                     && (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded)
                     && (p.StripePaymentIntentId != null || p.TransactionId.StartsWith("pi_")),
                ct);
        return paidThroughCasaZen ? amount : 0m;
    }

    /// <summary>
    /// Italy: who collects (<see cref="TouristTaxCollectorRules.Decide"/>: the active rule of the channel in the comune, then the host's declaration) and the
    /// calculation guest by guest of the stay as it is now; the confirmed amount only from a confirmed record that is not stale.
    /// </summary>
    private async Task<TaxFigures> ItalianFiguresAsync(Booking booking, StayTouristTax? record, StayTouristTaxView? tax, CancellationToken ct)
    {
        var currency = StayCurrency.ForProperty(booking.Property);
        var collector = await CollectorAsync(booking, record, ct);
        var view = tax ?? await CalculationAsync(booking.Id, ct);
        if (view is not { Status: TouristTaxQuoteStatus.Calculated, AmountDue: { } amountDue })
            return new TaxFigures(collector, null, null, currency, [], view?.Status ?? TouristTaxQuoteStatus.RateUnavailable);

        var confirmed = record is { ConfirmedAt: not null } && !view.Stale ? record.AmountDue : (decimal?)null;
        var lines = view.Guests
            .Select(g => new TouristTaxGuestLine(g.Position, g.TaxableNights, g.Amount, g.ExemptionKind != TouristTaxExemptionKind.None))
            .ToList();
        return new TaxFigures(collector, amountDue, confirmed, currency, lines, view.Status);
    }

    /// <summary>
    /// Switzerland: the channels the issuer's profile says collect the tax themselves (the shipped default while it confirmed none), else the host; the
    /// amount is the rule of the canton of the property for the guests and the nights of the stay (guests × nights × rate), a fixed rule the host does not
    /// confirm. No rule for the canton: not calculated.
    /// </summary>
    private async Task<TaxFigures> SwissFiguresAsync(Booking booking, CancellationToken ct)
    {
        var currency = StayCurrency.ForProperty(booking.Property);
        var profile = await db.OrgFiscalIssuerProfiles
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrgId == booking.OrgId, ct);
        var platformCollected = documentLines.PlatformCollectedChannelsOf(
            profile ?? new OrgFiscalIssuerProfile { OrgId = booking.OrgId, CountryCode = FiscalDocumentRules.Switzerland });
        if (platformCollected.Contains(booking.Source))
            return new TaxFigures(TouristTaxCollector.Channel, 0m, 0m, currency, [], TouristTaxQuoteStatus.Calculated);

        var municipality = booking.Property.SwissMunicipalityCode?.Trim();
        var canton = string.IsNullOrEmpty(municipality)
            ? null
            : await db.SwissMunicipalities
                .AsNoTracking()
                .Where(m => m.MunicipalityCode == municipality)
                .Select(m => m.CantonCode)
                .FirstOrDefaultAsync(ct);
        var checkIn = RomeCalendar.DateInRome(booking.CheckInDate);
        var nights = RomeCalendar.DateInRome(booking.CheckOutDate).DayNumber - checkIn.DayNumber;
        var guests = booking.NumberOfGuests;
        var rules = swissTouristTax.Value;
        if (rules.TaxOf(canton, checkIn, nights, guests) is not { } total || rules.TaxOf(canton, checkIn, nights, 1) is not { } each)
            return new TaxFigures(TouristTaxCollector.Host, null, null, currency, [], TouristTaxQuoteStatus.RateUnavailable);

        var lines = Enumerable.Range(0, guests).Select(position => new TouristTaxGuestLine(position, nights, each, Exempt: false)).ToList();
        return new TaxFigures(TouristTaxCollector.Host, total, total, currency, lines, TouristTaxQuoteStatus.Calculated);
    }

    /// <summary>Who collects the tax of an Italian stay: the one decision of <see cref="TouristTaxCollectorRules.Decide"/>.</summary>
    private async Task<TouristTaxCollector?> CollectorAsync(Booking booking, StayTouristTax? record, CancellationToken ct)
    {
        TouristTaxChannelRule? rule = null;
        var istatCode = booking.Property.ComuneIstatCode?.Trim();
        if (TouristTaxCollectorRules.IsChannelBooking(booking.Source) && !string.IsNullOrEmpty(istatCode))
        {
            rule = await db.TouristTaxChannelRules
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Channel == booking.Source && r.IstatCode == istatCode && r.IsActive, ct);
        }

        return TouristTaxCollectorRules.Decide(booking.Source, rule, record?.DeclaredCollector).Collector;
    }

    /// <summary>The calculation of the stay; <c>null</c> when it cannot be made (no guest registered yet, a stay of no night).</summary>
    private async Task<StayTouristTaxView?> CalculationAsync(Guid bookingId, CancellationToken ct)
    {
        try
        {
            return await stayTouristTax.GetAsync(bookingId, ct);
        }
        catch (DomainRuleException ex) when (ex.Code == StayTouristTaxService.NotCalculableCode)
        {
            return null;
        }
    }

    /// <summary>
    /// The open intent of the stay when it can still be paid for exactly <paramref name="amountMinor"/> in <paramref name="currency"/> on
    /// <paramref name="account"/> (kept); any other open one is cancelled first, so a guest never holds two payable intents of one stay. One that is
    /// being paid, or was paid, stops the request: the webhook records it.
    /// </summary>
    private async Task<PaymentIntent?> KeepOpenIntentAsync(
        StayTouristTax record,
        string account,
        long amountMinor,
        string currency,
        CancellationToken ct)
    {
        if (record.StripePaymentIntentId is not { } openIntentId)
            return null;

        var intentAccount = record.StripeAccountId ?? account;
        var intent = await stripe.GetPaymentIntentAsync(openIntentId, intentAccount, ct);
        if (intent.Status is { } moving && MovingIntentStatuses.Contains(moving))
            throw PaymentInProgress();

        if (intent.Status is { } status
            && PayableIntentStatuses.Contains(status)
            && intent.Amount == amountMinor
            && string.Equals(intent.Currency, StayCurrency.ForStripe(currency), StringComparison.OrdinalIgnoreCase)
            && string.Equals(intentAccount, account, StringComparison.Ordinal))
        {
            return intent;
        }

        if (!string.Equals(intent.Status, "canceled", StringComparison.Ordinal))
            await stripe.CancelPaymentIntentAsync(openIntentId, intentAccount, $"tourist-tax-replace:{record.Id:N}:{openIntentId}", ct);
        return null;
    }

    /// <summary>
    /// Refunds in full what the guest paid online, on the account of the intent, with the idempotency key of the stay's payment: a retry never refunds
    /// twice. A refusal of the provider that will not pass (4xx) is logged with its code and returns the reason of the host's «To do»; one that may pass
    /// (no answer, 429, 5xx) is thrown again, so the event is delivered again.
    /// </summary>
    private async Task<string?> RefundAsync(StayTouristTax record, decimal held, CancellationToken ct)
    {
        var paymentIntentId = record.StripePaymentIntentId!;
        try
        {
            var refund = await stripe.CreateRefundAsync(
                new StripeRefundCreateRequest(
                    paymentIntentId,
                    record.StripeAccountId,
                    TouristTaxCollectionRules.ToMinorUnits(held),
                    $"tourist-tax-refund:{record.Id:N}:{paymentIntentId}",
                    new Dictionary<string, string>
                    {
                        [TouristTaxCharges.KindMetadataKey] = TouristTaxCharges.Kind,
                        [TouristTaxCharges.BookingMetadataKey] = record.BookingId.ToString(),
                        [TouristTaxCharges.RecordMetadataKey] = record.Id.ToString(),
                    }),
                ct);
            if (string.Equals(refund.Status, "succeeded", StringComparison.Ordinal))
                record.RefundedAmount = record.CollectedAmount;

            logger.LogInformation(
                "Tourist tax of booking {BookingId}: refund {RefundId} of the online payment {PaymentIntentId} is {Status}",
                record.BookingId,
                refund.Id,
                paymentIntentId,
                refund.Status);
            return null;
        }
        catch (StripeException ex) when (string.Equals(ex.StripeError?.Code, "charge_already_refunded", StringComparison.Ordinal))
        {
            record.RefundedAmount = record.CollectedAmount;
            return null;
        }
        catch (StripeException ex) when (!IsTransient(ex))
        {
            logger.LogError(
                "Tourist tax of booking {BookingId}: the refund of the online payment {PaymentIntentId} was refused ({Code}, HTTP {HttpStatus}): the host gives it back",
                record.BookingId,
                paymentIntentId,
                ex.StripeError?.Code ?? "unknown",
                (int)ex.HttpStatusCode);
            return RefundFailedReason;
        }
        catch (StripeException ex)
        {
            logger.LogError(
                "Tourist tax of booking {BookingId}: the refund of the online payment {PaymentIntentId} failed ({Code}, HTTP {HttpStatus}): the event is delivered again",
                record.BookingId,
                paymentIntentId,
                ex.StripeError?.Code ?? "unknown",
                (int)ex.HttpStatusCode);
            throw;
        }
    }

    /// <summary>
    /// The host's «To do» that asks to give money back to the guest: once per booking, open or closed (a host who closed it after giving the money back does
    /// not get it again), with a fixed sentence in the language of the org and no data of the guest.
    /// </summary>
    private async Task RaiseRefundTodoAsync(Booking booking, string reasonKey, CancellationToken ct)
    {
        if (await BookingTodoItems.WasRaisedAsync(db, booking.OrgId, ActivityType.TouristTaxRefundDue, booking.Id, ct))
            return;

        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.TouristTaxRefundDue,
                Action = ActivityAction.ReviewBooking,
                EntityType = InternalActivity.BookingEntityType,
                EntityId = booking.Id,
                Urgency = ActivityUrgency.Medium,
                PropertyId = booking.PropertyId,
                Reason = texts.Get(reasonKey, DocumentLanguages.Normalize(booking.Org?.DefaultLanguage)),
            },
            ct);
        logger.LogInformation("Tourist tax of booking {BookingId}: a «To do» asks the host to give money back ({Reason})", booking.Id, reasonKey);
    }

    private void EnsureAvailable(Org org)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.TouristTaxOnlinePayment) || !IsOffered(org))
            throw NotAvailable();
    }

    /// <summary>The org offers the payment and its account takes payments (<see cref="Org.CanTakeDirectPayments"/>).</summary>
    private static bool IsOffered(Org org) => org.TouristTaxOnlinePaymentEnabled && org.CanTakeDirectPayments;

    private TouristTaxCollectionView ViewOf(Booking booking, StayTouristTax? record, TouristTaxCollectionDue due, TaxFigures figures) => new(
        due.Status,
        due.Reason,
        due.ToCollect,
        due.OnlinePayable,
        due.RefundDue,
        figures.AmountDue,
        figures.IncludedInBooking,
        record?.CollectedAmount ?? 0m,
        record?.RefundedAmount ?? 0m,
        record?.Currency ?? figures.Currency,
        record?.CollectedAt,
        featureFlags.IsEnabled(FeatureFlags.TouristTaxOnlinePayment) && IsOffered(booking.Org));

    private static TouristTaxSettingsView SettingsOf(Org org) =>
        new(org.TouristTaxOnlinePaymentEnabled, org.TouristTaxRequiredBeforeAccessCode, org.CanTakeDirectPayments);

    /// <summary>
    /// A record created by the collection: unconfirmed, like the host's declaration of the collector (no line and no amount until the host confirms the
    /// calculation), kept as long as a confirmed one.
    /// </summary>
    private static StayTouristTax NewRecord(Booking booking, TaxFigures figures, DateTime now)
    {
        var checkIn = RomeCalendar.DateInRome(booking.CheckInDate);
        var checkOut = RomeCalendar.DateInRome(booking.CheckOutDate);
        return new StayTouristTax
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            Status = figures.QuoteStatus,
            Nights = Math.Max(0, checkOut.DayNumber - checkIn.DayNumber),
            TaxableNights = 0,
            Total = 0m,
            AmountDue = 0m,
            RetainUntil = StayTouristTax.RetentionFor(checkOut),
            Currency = figures.Currency,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static Dictionary<string, string> MetadataOf(Booking booking, StayTouristTax record) => new()
    {
        [TouristTaxCharges.KindMetadataKey] = TouristTaxCharges.Kind,
        [TouristTaxCharges.BookingMetadataKey] = booking.Id.ToString(),
        [TouristTaxCharges.RecordMetadataKey] = record.Id.ToString(),
        [TouristTaxCharges.OrgMetadataKey] = booking.OrgId.ToString(),
        [TouristTaxCharges.PropertyMetadataKey] = booking.PropertyId.ToString(),
    };

    private static bool TryGetId(IReadOnlyDictionary<string, string> metadata, string key, out Guid id)
    {
        id = Guid.Empty;
        return metadata.TryGetValue(key, out var raw) && Guid.TryParse(raw, out id) && id != Guid.Empty;
    }

    /// <summary>No answer, too many requests or a failure of the provider: it may pass, so the event is delivered again.</summary>
    private static bool IsTransient(StripeException ex) => (int)ex.HttpStatusCode is 0 or 429 or >= 500;

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    private static DomainConflictException NotAvailable() =>
        new(TouristTaxCharges.NotAvailableCode, "TouristTaxPaymentNotAvailable");

    private static DomainConflictException PaymentInProgress() =>
        new(TouristTaxCharges.PaymentInProgressCode, "TouristTaxPaymentInProgress");

    private static NotFoundException BookingNotFound() =>
        new("Booking not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    /// <summary>The figures the collection of a stay is decided from (<see cref="IncludedInBooking"/> is set by <see cref="EvaluateAsync"/>).</summary>
    private sealed record TaxFigures(
        TouristTaxCollector? Collector,
        decimal? AmountDue,
        decimal? ConfirmedAmountDue,
        string Currency,
        IReadOnlyList<TouristTaxGuestLine> Lines,
        TouristTaxQuoteStatus QuoteStatus,
        decimal IncludedInBooking = 0m);
}
