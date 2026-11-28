using System.Runtime.ExceptionServices;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IBookingOctorateSyncService"/> (E08/F01/US01/T03, ADR-031, ADR-022-R4, R5, D13): the create of a CasaZen booking on Octorate, once,
/// with a state the host sees, and the retry the host asks for.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only what CasaZen owns.</b> A booking from Octorate or from a channel, one that is not confirmed or in the house, a stay that is over:
/// nothing. The target comes from <see cref="IOctorateBookingTargets"/>: a property that is not mirrored (no connected account, no confirmed mapping,
/// a mapping still to decide) gets no write and no sync row; a confirmed mapping without a single room records <c>Failed</c>
/// (<see cref="OctorateSyncErrorCodes.RoomNotChosen"/>).</item>
/// <item><b>Once.</b> Under the session lock <c>OctorateBookingSync</c> of the booking: the attempt is committed (<c>Pending</c>, one more attempt)
/// before the call, so a process that stops during the call leaves a trace. Octorate has no idempotency key (D13): an attempt that follows another
/// one looks the reservation up by its reference (the booking code, <c>refer</c>) before it creates anything, and a create whose answer was lost
/// (no answer, 429, 5xx) is looked up at once. Found is a success; never found with certainty, nothing is created again.</item>
/// <item><b>Failures, never a blind retry.</b> Octorate refusing the dates (409, 406): <c>Conflict</c> with
/// <see cref="OctorateSyncErrorCodes.DatesUnavailable"/> and one urgent "To do" on the booking (<see cref="ActivityType.BookingOverlap"/>), not
/// retried. Another refusal (4xx): <c>Failed</c>, <see cref="OctorateSyncErrorCodes.Rejected"/>, not retried. No answer and not found:
/// <c>Failed</c>, <see cref="OctorateSyncErrorCodes.Unavailable"/>, and the exception propagates so the outbox dispatcher delivers the event
/// again. The same holds for the read of the target itself (the rooms of the accommodation are read on Octorate): a failure there leaves a
/// <c>Failed</c> row too, never a booking with no trace.</item>
/// <item><b>Success</b>: <see cref="Booking.OctorateReservationId"/>, its <c>updateTime</c>, the state <c>Synced</c> in one save.</item>
/// <item><b>Payments are never lost.</b> After the reservation exists (the create just made, or one that an earlier attempt or the import
/// already linked), the payments of the booking paid before it, or whose recording failed, are recorded under the same lock. A payment that
/// cannot be recorded is not swallowed: the row is marked <c>Failed</c> with <see cref="OctorateSyncErrorCodes.PaymentsNotRecorded"/> (the host
/// sees it and can ask again) and the exception goes up where the outbox must deliver the event again.</item>
/// <item><b>Logs</b> carry ids, states and codes: never a name, an amount or a body.</item>
/// </list>
/// </remarks>
public sealed class BookingOctorateSyncService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IOctorateBookingTargets targets,
    IInternalActivityService activities,
    IDomainEventRecorder recorder,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<BookingOctorateSyncService> logger) : IBookingOctorateSyncService
{
    internal const string DatesTakenReasonKey = "TodoReason_OctorateDatesTaken";

    internal const string ConflictReasonKey = "TodoReason_BookingSyncConflict";

    private static readonly BookingStateMapper StayMapper = new();

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<OctorateSyncState?> PushCreateAsync(Guid bookingId, CancellationToken ct)
    {
        var booking = await ReadBookingAsync(bookingId, ct);
        if (booking is null)
        {
            logger.LogWarning("Octorate push of booking {BookingId}: the booking is not found, nothing is sent", bookingId);
            return null;
        }

        if (booking.OctorateReservationId is not null)
            return await CompleteReservedAsync(booking, ct);

        if (!IsMirrorable(booking.Source, booking.Status, booking.CheckOutDate))
            return null;

        OctorateTargetLookup lookup;
        try
        {
            lookup = await targets.LookupAsync(booking.PropertyId, ct);
        }
        catch (OctorateException ex)
        {
            // The lookup reads the rooms of the accommodation on Octorate. A failed read leaves the booking with a visible row (Failed, a code),
            // so the host can ask again: with no row it would have no state and no retry, and the event would be dead-lettered after five tries.
            var retry = ex is OctorateServerException or OctorateRateLimitException;
            await RecordLookupFailureAsync(
                booking, ex, retry ? OctorateSyncErrorCodes.Unavailable : OctorateSyncErrorCodes.Rejected, ct);
            if (retry)
                throw;

            return OctorateSyncState.Failed;
        }

        if (lookup.Gap == OctorateTargetGap.NotMirrored)
            return null;

        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                db, PostgresAdvisoryLocks.Scope.OctorateBookingSync, bookingId.ToString("N"), ct)
            ?? throw new OctorateSyncBusyException(bookingId);

        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == bookingId && s.OrgId == booking.OrgId, ct);
        var held = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.OctorateReservationId, b.Status })
            .FirstOrDefaultAsync(ct);
        if (held?.OctorateReservationId is not null)
            return sync?.State ?? OctorateSyncState.Synced;

        if (held is null || !IsMirrorable(booking.Source, held.Status, booking.CheckOutDate))
            return null;

        if (lookup.Target is not { } target)
            return await RecordAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.RoomNotChosen, ct);

        if (OctorateReservationRequests.DirectChannelIdOf(configuration) is null)
        {
            logger.LogError(
                "Octorate push of booking {BookingId}: {Key} is not set (Octorate__DirectChannelId), no reservation is created",
                bookingId,
                OctorateReservationRequests.DirectChannelIdKey);
            return await RecordAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.Unavailable, ct);
        }

        var mayExist = sync is not null && (sync.Attempts > 0 || sync.State == OctorateSyncState.Pending);
        sync = await MarkPendingAsync(booking, sync, ct);

        OctorateReservation? reservation;
        using (orgScope.Use(booking.OrgId))
        {
            try
            {
                reservation = mayExist
                    ? await client.FindReservationByExternalReferenceAsync(target.AccommodationId, booking.BookingCode, ct)
                    : null;
                reservation ??= await client.CreateReservationAsync(target, RequestOf(booking), ct);
            }
            catch (OctorateException ex) when (IsDatesRefusal(ex))
            {
                return await ConflictAsync(booking, sync, ct);
            }
            catch (OctorateException ex) when (ex is OctorateServerException or OctorateRateLimitException)
            {
                reservation = await TryFindAsync(target, booking, ct);
                if (reservation is null)
                {
                    await RecordFailureAsync(sync, OctorateSyncErrorCodes.Unavailable, ct);
                    logger.LogWarning(
                        "Octorate push of booking {BookingId}: no answer ({Code}) and the reservation is not found; the event is delivered again",
                        bookingId,
                        ex.Code);
                    throw;
                }
            }
            catch (OctorateException ex)
            {
                await RecordFailureAsync(sync, OctorateSyncErrorCodes.Rejected, ct);
                logger.LogWarning(
                    "Octorate push of booking {BookingId}: refused by Octorate ({Code}, HTTP {Status}), not retried",
                    bookingId,
                    ex.Code,
                    ex.OctorateStatusCode);
                return OctorateSyncState.Failed;
            }
            catch (ArgumentException ex)
            {
                await RecordFailureAsync(sync, OctorateSyncErrorCodes.Rejected, ct);
                logger.LogWarning(ex, "Octorate push of booking {BookingId}: the reservation cannot be written, not sent", bookingId);
                return OctorateSyncState.Failed;
            }
        }

        var attempts = sync.Attempts;
        if (!await RecordSyncedAsync(booking, sync, reservation, ct))
            return OctorateSyncState.Failed;

        logger.LogInformation(
            "Octorate push of booking {BookingId} of org {OrgId}: reservation {ReservationId} recorded (attempt {Attempt})",
            bookingId,
            booking.OrgId,
            reservation.Id,
            attempts);

        return await CatchUpPaymentsAsync(booking.OrgId, bookingId, target.AccommodationId, reservation.Id, ct);
    }

    public async Task PushPaymentAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await db.Payments
            .AsNoTracking()
            .Where(p => p.Id == paymentId)
            .Select(p => new { p.BookingId, p.Status, p.OctorateTransactionId })
            .FirstOrDefaultAsync(ct);
        if (payment is null || payment.OctorateTransactionId is not null || payment.Status != PaymentStatus.Completed)
            return;

        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == payment.BookingId)
            .Select(b => new { b.Id, b.OrgId, b.PropertyId, b.Status, b.Source, b.CheckOutDate, b.OctorateReservationId })
            .FirstOrDefaultAsync(ct);

        if (booking is null || FiscalCopy.IsOtaBookingSource(booking.Source))
            return;

        var state = await StateOfAsync(booking.Id, ct);

        if (state == OctorateSyncState.Pending)
            throw new OctorateSyncPendingException(booking.Id);

        if (booking.OctorateReservationId is not { } reservationId)
        {
            if (state is null
                && IsMirrorable(booking.Source, booking.Status, booking.CheckOutDate)
                && await CreateIsComingAsync(booking.OrgId, booking.Id, ct)
                && await targets.AccommodationOfAsync(booking.PropertyId, ct) is not null)
            {
                throw new OctorateSyncPendingException(booking.Id);
            }

            return;
        }

        if (state is null)
            return;

        var accommodation = await targets.AccommodationOfAsync(booking.PropertyId, ct);
        if (accommodation is null)
            return;

        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                db, PostgresAdvisoryLocks.Scope.OctorateBookingSync, booking.Id.ToString("N"), ct)
            ?? throw new OctorateSyncBusyException(booking.Id);

        if (state == OctorateSyncState.Failed)
        {
            await CatchUpPaymentsAsync(booking.OrgId, booking.Id, accommodation, reservationId, ct);
            return;
        }

        await RegisterPaymentAsync(booking.OrgId, accommodation, reservationId, paymentId, ct);
    }

    /// <summary>
    /// A booking that has its reservation on Octorate already (an earlier attempt made it, or the import matched its echo and linked it): the
    /// mirror is closed and the payments still to be recorded are recorded (they were paid before the reservation was there, or their recording
    /// failed). A reservation that came from Octorate or a channel has no row of ours: nothing is recorded on it.
    /// </summary>
    private async Task<OctorateSyncState?> CompleteReservedAsync(BookingFacts booking, CancellationToken ct)
    {
        var state = await StateOfAsync(booking.Id, ct);
        if (state is null || FiscalCopy.IsOtaBookingSource(booking.Source))
            return state ?? OctorateSyncState.Synced;

        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                db, PostgresAdvisoryLocks.Scope.OctorateBookingSync, booking.Id.ToString("N"), ct)
            ?? throw new OctorateSyncBusyException(booking.Id);

        var accommodation = await targets.AccommodationOfAsync(booking.PropertyId, ct);
        if (accommodation is null)
            return await CloseMirrorAsync(booking.OrgId, booking.Id, ct);

        return await CatchUpPaymentsAsync(booking.OrgId, booking.Id, accommodation, booking.OctorateReservationId!, ct);
    }

    /// <summary>
    /// The payments of a booking whose reservation exists, recorded under the lock the caller holds: those paid before the reservation was there
    /// and those whose recording failed. Every payment is tried, so one that cannot be recorded does not hold back the others. All recorded: the
    /// mirror is <c>Synced</c>. One failed: the row is marked <c>Failed</c> with <see cref="OctorateSyncErrorCodes.PaymentsNotRecorded"/> (the host
    /// sees it and can ask again) and the exception goes up where the outbox must deliver the event again (no answer, 429, 5xx, a missing
    /// <c>paymentMode</c>, the database); a refusal of Octorate is logged and left to the host.
    /// </summary>
    private async Task<OctorateSyncState> CatchUpPaymentsAsync(
        Guid orgId,
        Guid bookingId,
        string accommodationId,
        string reservationId,
        CancellationToken ct)
    {
        var (retry, refused) = await RegisterPaidPaymentsAsync(orgId, bookingId, accommodationId, reservationId, ct);
        if (retry is null && refused is null)
            return await CloseMirrorAsync(orgId, bookingId, ct);

        await MarkPaymentsNotRecordedAsync(orgId, bookingId, ct);
        if (retry is not null)
            ExceptionDispatchInfo.Capture(retry).Throw();

        return OctorateSyncState.Failed;
    }

    /// <summary>
    /// Records one paid payment on the reservation, the caller holding the lock of the booking: read again (it may have been recorded meanwhile),
    /// looked for on the reservation first (a registration whose answer was lost is already there, D13), then sent; the transaction is kept on the
    /// payment, unique in the org. What is recorded is the part of the payment that is not tourist tax (<see cref="AmountOnReservationAsync"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The <c>paymentMode</c> of the way the payment was collected is not configured.</exception>
    private async Task RegisterPaymentAsync(Guid orgId, string accommodationId, string reservationId, Guid paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId && p.OrgId == orgId, ct);
        if (payment is null || payment.OctorateTransactionId is not null || payment.Status != PaymentStatus.Completed)
            return;

        var transaction = TransactionOf(payment);
        var collectedBy = CollectedByOf(payment);
        var mode = collectedBy is null ? null : OctorateReservationRequests.PaymentModeOf(configuration, collectedBy);
        if (mode is null)
        {
            throw new InvalidOperationException(
                $"{OctorateReservationRequests.PaymentModesSection}:{collectedBy ?? payment.Method.ToString()} is not configured " +
                "(Octorate__PaymentModes__*, docs/runbooks/deploy-checklist.md): the payment is not recorded on Octorate.");
        }

        var amount = await AmountOnReservationAsync(payment, ct);
        if (amount <= 0m)
        {
            logger.LogInformation(
                "Octorate payment {PaymentId} of org {OrgId}: it pays tourist tax only, nothing is recorded on reservation {ReservationId}",
                paymentId,
                orgId,
                reservationId);
            return;
        }

        using (orgScope.Use(orgId))
        {
            var reservation = await client.GetReservationAsync(accommodationId, reservationId, ct);
            if (!reservation.PaymentTransactions.Contains(transaction, StringComparer.Ordinal))
            {
                await client.RegisterPaymentAsync(
                    accommodationId,
                    reservationId,
                    new OctoratePaymentRegistration(transaction, mode, amount, payment.ProcessedAt ?? payment.UpdatedAt),
                    ct);
            }
        }

        payment.OctorateTransactionId = transaction;
        payment.OctorateRegisteredAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Octorate payment {PaymentId} of org {OrgId}: recorded on reservation {ReservationId} ({Mode})", paymentId, orgId, reservationId, mode);
    }

    /// <summary>
    /// What of a payment goes on the reservation of Octorate. The reservation holds the stay (<c>roomGross</c> is <see cref="Booking.BasePrice"/>) and
    /// Octorate works out the tourist tax itself (its create has no such member), while the guest pays <see cref="Booking.TotalPrice"/> to CasaZen.
    /// So the part of the payments that is tourist tax is left out and the payments recorded on a reservation never pass its price by the tax:
    /// the tax is the last <see cref="Booking.TouristTax"/> of the price, taken from the payments of the booking in the order they came in
    /// (<see cref="OctorateReservationRequests.AmountNetOfTouristTax"/>). Zero when the payment pays tourist tax only.
    /// </summary>
    private async Task<decimal> AmountOnReservationAsync(Payment payment, CancellationToken ct)
    {
        var price = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == payment.BookingId && b.OrgId == payment.OrgId)
            .Select(b => new { b.TotalPrice, b.TouristTax })
            .FirstOrDefaultAsync(ct);
        if (price is null || price.TouristTax <= 0m)
            return OctorateReservationRequests.Money(payment.Amount);

        var others = await db.Payments
            .AsNoTracking()
            .Where(p => p.OrgId == payment.OrgId
                        && p.BookingId == payment.BookingId
                        && p.Status == PaymentStatus.Completed
                        && p.Id != payment.Id)
            .Select(p => new { p.Id, p.Amount, p.ProcessedAt, p.CreatedAt })
            .ToListAsync(ct);
        var at = payment.ProcessedAt ?? payment.CreatedAt;
        var paidBefore = others
            .Where(p =>
            {
                var when = p.ProcessedAt ?? p.CreatedAt;
                return when < at || (when == at && p.Id.CompareTo(payment.Id) < 0);
            })
            .Sum(p => p.Amount);

        return OctorateReservationRequests.AmountNetOfTouristTax(payment.Amount, paidBefore, price.TotalPrice, price.TouristTax);
    }

    /// <summary>
    /// Records the paid payments of a booking that are not on Octorate yet, in the order they came in. A payment that cannot be recorded does not
    /// stop the others, and its error is not lost: the first one that the outbox must deliver again is returned as <c>Retry</c> (no answer, 429,
    /// 5xx, a missing <c>paymentMode</c>, the database), the first one that only a person can settle (Octorate refused it) as <c>Refused</c>.
    /// </summary>
    private async Task<(Exception? Retry, Exception? Refused)> RegisterPaidPaymentsAsync(
        Guid orgId,
        Guid bookingId,
        string accommodationId,
        string reservationId,
        CancellationToken ct)
    {
        var paid = await db.Payments
            .AsNoTracking()
            .Where(p => p.OrgId == orgId
                        && p.BookingId == bookingId
                        && p.Status == PaymentStatus.Completed
                        && p.OctorateTransactionId == null)
            .OrderBy(p => p.ProcessedAt ?? p.CreatedAt)
            .ThenBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(ct);

        Exception? retry = null;
        Exception? refused = null;
        foreach (var paymentId in paid)
        {
            try
            {
                await RegisterPaymentAsync(orgId, accommodationId, reservationId, paymentId, ct);
            }
            catch (Exception ex) when (ex is OctorateException or InvalidOperationException or ArgumentException or DbUpdateException)
            {
                logger.LogWarning(
                    "Octorate push of booking {BookingId}: payment {PaymentId} not recorded ({Error})", bookingId, paymentId, ex.GetType().Name);
                if (ex is ArgumentException or (OctorateException and not (OctorateServerException or OctorateRateLimitException)))
                    refused ??= ex;
                else
                    retry ??= ex;

                db.ChangeTracker.Clear();
            }
        }

        return (retry, refused);
    }

    /// <summary>The <c>transaction</c> of a payment on Octorate: the Stripe PaymentIntent id, or <c>casazen-{paymentId}</c> for one the host recorded.</summary>
    internal static string TransactionOf(Payment payment) =>
        string.IsNullOrWhiteSpace(payment.StripePaymentIntentId) ? $"casazen-{payment.Id}" : payment.StripePaymentIntentId;

    internal static string? CollectedByOf(Payment payment)
    {
        if (!string.IsNullOrWhiteSpace(payment.StripePaymentIntentId))
            return "Stripe";

        return payment.Method switch
        {
            PaymentMethod.CashOnArrival => "CashOnArrival",
            PaymentMethod.BankTransfer => "BankTransfer",
            _ => null,
        };
    }

    public async Task<OctorateSyncState?> PushUpdateAsync(Guid bookingId, bool askedAgain, bool overwrite, CancellationToken ct)
    {
        var booking = await ReadBookingAsync(bookingId, ct);
        if (booking is null)
        {
            logger.LogWarning("Octorate update of booking {BookingId}: the booking is not found, nothing is sent", bookingId);
            return null;
        }

        if (!IsMirrorable(booking.Source, booking.Status, booking.CheckOutDate))
            return null;

        var row = await db.OctorateBookingSyncs
            .AsNoTracking()
            .Where(s => s.BookingId == bookingId)
            .Select(s => new { s.State, s.LastOperation })
            .FirstOrDefaultAsync(ct);

        // The reservation is not on Octorate yet. The create under way, or on its way (the confirmation still waits in the outbox), writes the booking as
        // it is now: this event is delivered again after it, never consumed with no effect (events arrive out of order, ADR-022). A create that failed, or a
        // property that is not mirrored, has nothing to update: the create, when it succeeds, sends the current data.
        if (booking.OctorateReservationId is null)
        {
            if (row?.State == OctorateSyncState.Pending
                || (row is null
                    && await CreateIsComingAsync(booking.OrgId, bookingId, ct)
                    && await targets.AccommodationOfAsync(booking.PropertyId, ct) is not null))
            {
                throw new OctorateSyncPendingException(bookingId);
            }

            return null;
        }

        if (row is null || (askedAgain && row.LastOperation != OctorateSyncOperation.Update))
            return null;

        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                db, PostgresAdvisoryLocks.Scope.OctorateBookingSync, bookingId.ToString("N"), ct)
            ?? throw new OctorateSyncBusyException(bookingId);

        var orgId = booking.OrgId;
        booking = await ReadBookingAsync(bookingId, ct);
        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == bookingId && s.OrgId == orgId, ct);
        if (booking is null || sync is null || booking.OctorateReservationId is not { } reservationId
            || !IsMirrorable(booking.Source, booking.Status, booking.CheckOutDate))
        {
            return null;
        }

        OctorateTargetLookup lookup;
        try
        {
            lookup = await targets.LookupAsync(booking.PropertyId, ct);
        }
        catch (OctorateException ex)
        {
            // The rooms of the accommodation are read on Octorate: a failed read leaves a visible row, never a change with no trace.
            var retry = ex is OctorateServerException or OctorateRateLimitException;
            await RecordUpdateAsync(booking, sync, OctorateSyncState.Failed, retry ? OctorateSyncErrorCodes.Unavailable : OctorateSyncErrorCodes.Rejected, ct);
            logger.LogWarning(
                "Octorate update of booking {BookingId}: the target could not be read ({Code}, HTTP {Status})", bookingId, ex.Code, ex.OctorateStatusCode);
            if (retry)
                throw;

            return OctorateSyncState.Failed;
        }

        // Not mirrored now (the connection was lost or is in error, a mapping still to decide or write-blocked): nothing is ever written to Octorate.
        // The reservation exists there, so the change is not consumed with no trace: the row says it was not sent and the host asks again once the
        // connection or the mapping is back (the retry runs this again).
        if (lookup.Gap == OctorateTargetGap.NotMirrored)
        {
            logger.LogWarning(
                "Octorate update of booking {BookingId} of org {OrgId}: its property is not mirrored now, nothing is sent; the host asks again", bookingId, booking.OrgId);
            return await RecordUpdateAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.NotMirrored, ct);
        }

        if (lookup.Target is not { } target)
            return await RecordUpdateAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.RoomNotChosen, ct);

        // The version the change is based on. A conflict is only ever settled by a person who has seen both versions, in one of two ways. «Keep mine»
        // (overwrite) is based on the version Octorate held when the conflict was found, so it writes over exactly that one. «Take theirs» is the
        // booking aligned to that version (the stay and the guests the row kept): there is nothing to write, the version is adopted. Any other change
        // after the conflict (the notes, a price, another stay) and a plain retry are based on the version CasaZen last saw: they conflict again,
        // nothing is written and the «To do» stays open. The edit never overwrites Octorate by itself.
        var conflict = sync.State == OctorateSyncState.Conflict;
        if (conflict && !overwrite && MatchesTheirVersion(booking, sync))
            return await AdoptTheirVersionAsync(booking, sync, ct);

        var seen = conflict && overwrite;
        var basedOn = (seen ? sync.OctorateUpdateTime : null) ?? booking.OctorateUpdatedAt ?? sync.OctorateUpdateTime;

        var carriesPaymentFailure = sync.State == OctorateSyncState.Failed && sync.LastOperation == OctorateSyncOperation.Payment;

        sync = await MarkUpdatePendingAsync(booking, sync, ct);

        OctorateReservation reservation;
        using (orgScope.Use(booking.OrgId))
        {
            try
            {
                reservation = await client.UpdateReservationAsync(target, reservationId, UpdateOf(booking, basedOn), ct);
            }
            catch (OctorateConflictException)
            {
                return await UpdateConflictAsync(booking, sync, target, reservationId, ct);
            }
            catch (OctorateException ex) when (ex is OctorateServerException or OctorateRateLimitException)
            {
                // No answer, a 429 or a 5xx: the change may or may not have reached Octorate. It is idempotent (the same values are written), so the
                // event is delivered again.
                await RecordUpdateAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.Unavailable, ct);
                logger.LogWarning(
                    "Octorate update of booking {BookingId}: no answer ({Code}, HTTP {Status}); the event is delivered again", bookingId, ex.Code, ex.OctorateStatusCode);
                throw;
            }
            catch (OctorateException ex)
            {
                await RecordUpdateAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.Rejected, ct);
                logger.LogWarning(
                    "Octorate update of booking {BookingId}: refused by Octorate ({Code}, HTTP {Status}), not retried", bookingId, ex.Code, ex.OctorateStatusCode);
                return OctorateSyncState.Failed;
            }
            catch (ArgumentException ex)
            {
                await RecordUpdateAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.Rejected, ct);
                logger.LogWarning(ex, "Octorate update of booking {BookingId}: the change cannot be written, not sent", bookingId);
                return OctorateSyncState.Failed;
            }
        }

        await RecordUpdateSyncedAsync(booking, sync, reservation, ct);
        logger.LogInformation(
            "Octorate update of booking {BookingId} of org {OrgId}: reservation {ReservationId} changed (attempt {Attempt})",
            bookingId,
            booking.OrgId,
            reservationId,
            sync.Attempts);

        return carriesPaymentFailure
            ? await CatchUpPaymentsAsync(booking.OrgId, bookingId, target.AccommodationId, reservationId, ct)
            : OctorateSyncState.Synced;
    }

    private static bool MatchesTheirVersion(BookingFacts booking, OctorateBookingSync sync)
    {
        if (sync.OctorateUpdateTime is null || OctorateVersionSnapshot.Parse(sync.OctorateVersionJson) is not { } theirs)
            return false;

        return theirs.CheckIn is { } checkIn
               && theirs.CheckOut is { } checkOut
               && theirs.Guests is { } guests
               && checkIn == RomeCalendar.DateInRome(booking.CheckInDate)
               && checkOut == RomeCalendar.DateInRome(booking.CheckOutDate)
               && guests == Math.Max(1, booking.Guests);
    }

    private async Task<OctorateSyncState> AdoptTheirVersionAsync(BookingFacts booking, OctorateBookingSync sync, CancellationToken ct)
    {
        var now = UtcNow;
        var tracked = await db.Bookings.FirstAsync(b => b.Id == booking.Id && b.OrgId == booking.OrgId, ct);
        tracked.OctorateUpdatedAt = sync.OctorateUpdateTime ?? tracked.OctorateUpdatedAt;

        sync.State = OctorateSyncState.Synced;
        sync.LastOperation = OctorateSyncOperation.Update;
        sync.ErrorCode = null;
        sync.OctorateVersionJson = null;
        sync.LastSyncedAt = now;
        sync.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await CloseConflictTodosAsync(booking, ct);
        logger.LogInformation(
            "Octorate update of booking {BookingId} of org {OrgId}: the booking holds the version Octorate has, adopted with nothing written",
            booking.Id,
            booking.OrgId);
        return OctorateSyncState.Synced;
    }

    /// <summary>
    /// Octorate refused the change because the reservation holds another version: it is read again, the dates and the guests Octorate holds are kept on the
    /// row (<see cref="OctorateBookingSync.OctorateVersionJson"/>) with its <c>updateTime</c>, the state is <c>Conflict</c> and the host gets a «To do» on the
    /// booking. Neither version is lost, and nothing is sent again by itself.
    /// </summary>
    private async Task<OctorateSyncState> UpdateConflictAsync(
        BookingFacts booking,
        OctorateBookingSync sync,
        OctorateBookingTarget target,
        string reservationId,
        CancellationToken ct)
    {
        Exception? readFailure = null;
        try
        {
            var theirs = await client.GetReservationAsync(target, reservationId, ct);
            sync.OctorateVersionJson = SnapshotOf(theirs)?.ToJson();
            sync.OctorateUpdateTime = theirs.UpdatedAt ?? sync.OctorateUpdateTime;
        }
        catch (OctorateException ex)
        {
            readFailure = ex;
        }

        var now = UtcNow;
        sync.State = OctorateSyncState.Conflict;
        sync.LastOperation = OctorateSyncOperation.Update;
        sync.ErrorCode = OctorateSyncErrorCodes.ReservationChanged;
        sync.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Octorate update of booking {BookingId}: the reservation was changed on Octorate, nothing is overwritten", booking.Id);

        // One «To do» on the booking: a second attempt finds the open item instead of raising another (CreateAsync is idempotent).
        try
        {
            await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.BookingSyncConflict,
                    Action = ActivityAction.ReviewBooking,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = booking.Id,
                    Urgency = ActivityUrgency.High,
                    PropertyId = booking.PropertyId,
                    Reason = await BookingTodoItems.ReasonAsync(db, booking.OrgId, ConflictReasonKey, ct),
                },
                ct);
        }
        catch (NotFoundException)
        {
            logger.LogInformation("Octorate update of booking {BookingId}: its property is gone, no To do", booking.Id);
        }

        if (readFailure is OctorateServerException or OctorateRateLimitException)
            ExceptionDispatchInfo.Capture(readFailure).Throw();

        if (readFailure is not null)
        {
            logger.LogWarning(
                "Octorate update of booking {BookingId}: the version Octorate holds could not be read ({Error})", booking.Id, readFailure.GetType().Name);
        }

        return OctorateSyncState.Conflict;
    }

    private async Task RecordUpdateSyncedAsync(BookingFacts booking, OctorateBookingSync sync, OctorateReservation reservation, CancellationToken ct)
    {
        var now = UtcNow;
        var tracked = await db.Bookings.FirstAsync(b => b.Id == booking.Id && b.OrgId == booking.OrgId, ct);
        tracked.OctorateUpdatedAt = reservation.UpdatedAt ?? tracked.OctorateUpdatedAt;

        sync.State = OctorateSyncState.Synced;
        sync.LastOperation = OctorateSyncOperation.Update;
        sync.ErrorCode = null;
        sync.OctorateUpdateTime = reservation.UpdatedAt ?? sync.OctorateUpdateTime;
        sync.OctorateVersionJson = null;
        sync.LastSyncedAt = now;
        sync.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await CloseConflictTodosAsync(booking, ct);
    }

    private async Task CloseConflictTodosAsync(BookingFacts booking, CancellationToken ct)
    {
        var typeName = nameof(ActivityType.BookingSyncConflict);
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == booking.OrgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == typeName
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId == booking.Id)
            .Select(a => a.Id)
            .ToListAsync(ct);
        foreach (var id in open)
        {
            try
            {
                await activities.CloseAsync(id, InternalActivity.ClosedBySystem, ct);
            }
            catch (NotFoundException)
            {
                logger.LogInformation("Octorate update of booking {BookingId}: the To do {ActivityId} is gone", booking.Id, id);
            }
        }
    }

    private async Task<OctorateBookingSync> MarkUpdatePendingAsync(BookingFacts booking, OctorateBookingSync sync, CancellationToken ct)
    {
        var now = UtcNow;
        sync.State = OctorateSyncState.Pending;
        sync.LastOperation = OctorateSyncOperation.Update;
        sync.PropertyId = booking.PropertyId;
        sync.Attempts++;
        sync.LastAttemptAt = now;
        sync.ErrorCode = null;
        sync.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return sync;
    }

    private async Task<OctorateSyncState> RecordUpdateAsync(
        BookingFacts booking,
        OctorateBookingSync sync,
        OctorateSyncState state,
        string errorCode,
        CancellationToken ct)
    {
        sync.State = state;
        sync.LastOperation = OctorateSyncOperation.Update;
        sync.PropertyId = booking.PropertyId;
        sync.ErrorCode = errorCode;
        sync.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Octorate update of booking {BookingId}: {State} ({Code})", booking.Id, state, errorCode);
        return state;
    }

    private static OctorateReservationUpdate UpdateOf(BookingFacts booking, DateTime? basedOn)
    {
        var guests = Math.Max(1, booking.Guests);
        return new OctorateReservationUpdate
        {
            CheckIn = RomeCalendar.DateInRome(booking.CheckInDate),
            CheckOut = RomeCalendar.DateInRome(booking.CheckOutDate),
            Guests = guests,
            Children = Math.Clamp(booking.Children, 0, guests - 1),
            RoomGross = booking.BasePrice,
            Notes = booking.Notes,
            BasedOnUpdateTime = basedOn,
        };
    }

    private static OctorateVersionSnapshot? SnapshotOf(OctorateReservation reservation)
    {
        var checkIn = StayMapper.ToStayDate(reservation.CheckIn, null) is { } arrival ? DateOnly.FromDateTime(arrival) : (DateOnly?)null;
        var checkOut = StayMapper.ToStayDate(reservation.CheckOut, null) is { } departure ? DateOnly.FromDateTime(departure) : (DateOnly?)null;
        int? guests = reservation.TotalGuests is >= 1 ? reservation.TotalGuests : null;
        return checkIn is null && checkOut is null && guests is null ? null : new OctorateVersionSnapshot(checkIn, checkOut, guests);
    }

    public async Task<OctorateSyncState> RequeueAsync(Guid bookingId, bool overwrite, CancellationToken ct)
    {
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, ct)
            ?? throw new NotFoundException("Booking not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == bookingId && s.OrgId == booking.OrgId, ct);
        if (sync is null || !OctorateBookingSync.IsRetryable(sync.State))
        {
            throw new DomainConflictException(
                OctorateSyncErrorCodes.NotRetryable, OctorateSyncErrorCodes.MessageKeys[OctorateSyncErrorCodes.NotRetryable]);
        }

        // The row keeps its state: the push marks it Pending when it really starts (MarkPendingAsync). A push that then gives up (the booking was
        // cancelled, its stay is over, the property is no longer mirrored) leaves it Failed or Conflict, so the host can ask again and is never
        // stuck on a Pending that nothing completes.
        var now = UtcNow;

        recorder.Record(
            DomainEventType.OctorateSyncRequested,
            booking.Id,
            BookingEventPayload.Of(booking, BookingEventOrigin.System) with { Overwrite = overwrite },
            $"octorate-sync-retry:{booking.Id:N}:{now:O}",
            booking.OrgId);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Octorate push of booking {BookingId} asked again (last operation {Operation}, overwrite {Overwrite})",
            bookingId,
            sync.LastOperation,
            overwrite);
        return OctorateSyncState.Pending;
    }

    private Task<BookingFacts?> ReadBookingAsync(Guid bookingId, CancellationToken ct) =>
        db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new BookingFacts(
                b.Id,
                b.OrgId,
                b.PropertyId,
                b.Status,
                b.Source,
                b.OctorateReservationId,
                b.BookingCode,
                b.CheckInDate,
                b.CheckOutDate,
                b.NumberOfGuests,
                b.NumberOfChildren,
                b.BasePrice,
                b.Guest.FirstName,
                b.Guest.LastName,
                b.Guest.Gender,
                b.UpdatedAt,
                b.OctorateUpdatedAt,
                b.SpecialRequests))
            .FirstOrDefaultAsync(ct);

    private async Task<OctorateSyncState?> StateOfAsync(Guid bookingId, CancellationToken ct) =>
        await db.OctorateBookingSyncs
            .AsNoTracking()
            .Where(s => s.BookingId == bookingId)
            .Select(s => (OctorateSyncState?)s.State)
            .FirstOrDefaultAsync(ct);

    private bool IsMirrorable(BookingSource source, BookingStatus status, DateTime checkOutDate) =>
        !FiscalCopy.IsOtaBookingSource(source)
        && (status is BookingStatus.Confirmed or BookingStatus.CheckedIn)
        && checkOutDate >= timeProvider.TodayInRome();

    private Task<bool> CreateIsComingAsync(Guid orgId, Guid bookingId, CancellationToken ct) =>
        db.OutboxEvents
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                e => e.OrgId == orgId
                     && e.EntityId == bookingId
                     && e.EventType == DomainEventType.BookingConfirmed
                     && e.DispatchedAt == null
                     && !e.DeadLettered,
                ct);

    private static OctorateReservationCreate RequestOf(BookingFacts booking)
    {
        var guests = Math.Max(1, booking.Guests);
        return new OctorateReservationCreate
        {
            ExternalReference = booking.BookingCode,
            CheckIn = RomeCalendar.DateInRome(booking.CheckInDate),
            CheckOut = RomeCalendar.DateInRome(booking.CheckOutDate),
            Guests = guests,
            Children = Math.Clamp(booking.Children, 0, guests - 1),
            RoomGross = booking.BasePrice,
            BookerFirstName = booking.FirstName,
            BookerLastName = booking.LastName,
            BookerGender = booking.Gender,
        };
    }

    private static bool IsDatesRefusal(OctorateException exception) =>
        exception is OctorateConflictException || exception.OctorateStatusCode == OctorateApiClient.NotAvailableStatus;

    private async Task<OctorateReservation?> TryFindAsync(OctorateBookingTarget target, BookingFacts booking, CancellationToken ct)
    {
        try
        {
            return await client.FindReservationByExternalReferenceAsync(target.AccommodationId, booking.BookingCode, ct);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning("Octorate push of booking {BookingId}: the lookup by reference failed ({Code})", booking.Id, ex.Code);
            return null;
        }
    }

    private async Task<OctorateBookingSync> MarkPendingAsync(BookingFacts booking, OctorateBookingSync? sync, CancellationToken ct)
    {
        var now = UtcNow;
        sync ??= NewSync(booking, now);
        sync.State = OctorateSyncState.Pending;
        sync.LastOperation = OctorateSyncOperation.Create;
        sync.PropertyId = booking.PropertyId;
        sync.Attempts++;
        sync.LastAttemptAt = now;
        sync.ErrorCode = null;
        sync.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return sync;
    }

    private async Task<OctorateSyncState> RecordAsync(
        BookingFacts booking,
        OctorateBookingSync? sync,
        OctorateSyncState state,
        string errorCode,
        CancellationToken ct)
    {
        var now = UtcNow;
        sync ??= NewSync(booking, now);
        sync.State = state;
        sync.LastOperation = OctorateSyncOperation.Create;
        sync.PropertyId = booking.PropertyId;
        sync.ErrorCode = errorCode;
        sync.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Octorate push of booking {BookingId}: {State} ({Code})", booking.Id, state, errorCode);
        return state;
    }

    private async Task RecordLookupFailureAsync(BookingFacts booking, OctorateException error, string errorCode, CancellationToken ct)
    {
        logger.LogWarning(
            "Octorate push of booking {BookingId}: the target could not be read from Octorate ({Code}, HTTP {Status}); recorded as {ErrorCode}",
            booking.Id,
            error.Code,
            error.OctorateStatusCode,
            errorCode);

        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                db, PostgresAdvisoryLocks.Scope.OctorateBookingSync, booking.Id.ToString("N"), ct)
            ?? throw new OctorateSyncBusyException(booking.Id);

        var reservationId = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == booking.Id)
            .Select(b => b.OctorateReservationId)
            .FirstOrDefaultAsync(ct);
        if (reservationId is not null)
            return;

        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == booking.Id && s.OrgId == booking.OrgId, ct);
        await RecordAsync(booking, sync, OctorateSyncState.Failed, errorCode, ct);
    }

    private async Task RecordFailureAsync(OctorateBookingSync sync, string errorCode, CancellationToken ct)
    {
        sync.State = OctorateSyncState.Failed;
        sync.ErrorCode = errorCode;
        sync.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<OctorateSyncState> ConflictAsync(BookingFacts booking, OctorateBookingSync sync, CancellationToken ct)
    {
        sync.State = OctorateSyncState.Conflict;
        sync.ErrorCode = OctorateSyncErrorCodes.DatesUnavailable;
        sync.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Octorate push of booking {BookingId}: the dates are taken on Octorate, not retried", booking.Id);

        // One urgent "To do" on the booking: a second attempt finds the open item instead of raising another (CreateAsync is idempotent).
        try
        {
            await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.BookingOverlap,
                    Action = ActivityAction.ReviewBooking,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = booking.Id,
                    Urgency = ActivityUrgency.High,
                    PropertyId = booking.PropertyId,
                    Reason = await BookingTodoItems.ReasonAsync(db, booking.OrgId, DatesTakenReasonKey, ct),
                },
                ct);
        }
        catch (NotFoundException)
        {
            logger.LogInformation("Octorate push of booking {BookingId}: its property is gone, no To do", booking.Id);
        }

        return OctorateSyncState.Conflict;
    }

    private async Task<bool> RecordSyncedAsync(BookingFacts booking, OctorateBookingSync sync, OctorateReservation reservation, CancellationToken ct)
    {
        var now = UtcNow;
        var tracked = await db.Bookings.FirstAsync(b => b.Id == booking.Id && b.OrgId == booking.OrgId, ct);
        tracked.OctorateReservationId = reservation.Id;
        tracked.OctorateUpdatedAt = reservation.UpdatedAt ?? tracked.OctorateUpdatedAt;

        sync.State = OctorateSyncState.Synced;
        sync.ErrorCode = null;
        sync.OctorateUpdateTime = reservation.UpdatedAt;
        sync.LastSyncedAt = now;
        sync.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            if (await RecoverLinkAsync(booking, reservation, ct) is not { } linked)
                throw;

            return linked;
        }
    }

    /// <summary>
    /// The save of the reservation id failed, usually on the unique index of (org, Octorate id): the import read the reservation before the id was
    /// saved and holds it on a booking. The holder is read again. This booking itself (the import matched its echo by reference and linked it):
    /// the mirror is closed, <c>true</c>. Another booking (a duplicate made before the import matched by reference): nothing is moved by guess, the
    /// mirror is marked <c>Failed</c> with <see cref="OctorateSyncErrorCodes.ReservationLinkedElsewhere"/>, <c>false</c>, never a <c>Pending</c> that
    /// nothing completes. Nobody holds it, so the index was not the cause: <c>null</c>, and the caller raises the error.
    /// </summary>
    private async Task<bool?> RecoverLinkAsync(BookingFacts booking, OctorateReservation reservation, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        var holder = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == booking.OrgId && b.OctorateReservationId == reservation.Id)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(ct);
        if (holder is null)
            return null;

        var sync = await db.OctorateBookingSyncs.FirstAsync(s => s.BookingId == booking.Id && s.OrgId == booking.OrgId, ct);
        var now = UtcNow;
        var linked = holder == booking.Id;
        if (linked)
        {
            sync.State = OctorateSyncState.Synced;
            sync.ErrorCode = null;
            sync.OctorateUpdateTime = reservation.UpdatedAt;
            sync.LastSyncedAt = now;
        }
        else
        {
            sync.State = OctorateSyncState.Failed;
            sync.ErrorCode = OctorateSyncErrorCodes.ReservationLinkedElsewhere;
            logger.LogWarning(
                "Octorate push of booking {BookingId}: reservation {ReservationId} is already held by booking {HolderId}, nothing is linked by guess",
                booking.Id,
                reservation.Id,
                holder);
        }

        sync.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return linked;
    }

    private async Task<OctorateSyncState> CloseMirrorAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == bookingId && s.OrgId == orgId, ct);
        if (sync is null)
            return OctorateSyncState.Synced;

        if (sync.State != OctorateSyncState.Synced && sync.LastOperation is OctorateSyncOperation.Create or OctorateSyncOperation.Payment)
        {
            var now = UtcNow;
            sync.State = OctorateSyncState.Synced;
            sync.ErrorCode = null;
            sync.LastSyncedAt = now;
            sync.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return sync.State;
    }

    /// <summary>
    /// A payment of the booking could not be recorded: its row is <c>Failed</c> with <see cref="OctorateSyncErrorCodes.PaymentsNotRecorded"/>, so the
    /// host sees it and can ask again (the retry records what is still to be recorded). The reservation itself exists.
    /// </summary>
    private async Task MarkPaymentsNotRecordedAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == bookingId && s.OrgId == orgId, ct);
        if (sync is null)
            return;

        sync.State = OctorateSyncState.Failed;
        sync.LastOperation = OctorateSyncOperation.Payment;
        sync.ErrorCode = OctorateSyncErrorCodes.PaymentsNotRecorded;
        sync.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Octorate push of booking {BookingId}: its reservation exists but its payments are not all recorded ({Code})",
            bookingId,
            OctorateSyncErrorCodes.PaymentsNotRecorded);
    }

    private OctorateBookingSync NewSync(BookingFacts booking, DateTime now)
    {
        var sync = new OctorateBookingSync
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            PropertyId = booking.PropertyId,
            LastOperation = OctorateSyncOperation.Create,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OctorateBookingSyncs.Add(sync);
        return sync;
    }

    private sealed record BookingFacts(
        Guid Id,
        Guid OrgId,
        Guid PropertyId,
        BookingStatus Status,
        BookingSource Source,
        string? OctorateReservationId,
        string BookingCode,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        int Guests,
        int Children,
        decimal BasePrice,
        string FirstName,
        string LastName,
        Gender? Gender,
        DateTime UpdatedAt,
        DateTime? OctorateUpdatedAt,
        string? Notes);
}

public sealed class OctorateSyncBusyException(Guid bookingId)
    : Exception($"Booking {bookingId} is being sent to Octorate by another worker: delivered again later.");

/// <summary>
/// A payment of a booking whose reservation is still being created on Octorate, or is about to be (E08/F01/US01/T04): the event is delivered
/// again after the create, so the payment is never dropped and never recorded twice.
/// </summary>
public sealed class OctorateSyncPendingException(Guid bookingId)
    : Exception($"The reservation of booking {bookingId} is not on Octorate yet: the payment is delivered again later.");
