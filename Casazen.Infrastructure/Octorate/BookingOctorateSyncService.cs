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
/// again.</item>
/// <item><b>Success</b>: <see cref="Booking.OctorateReservationId"/>, its <c>updateTime</c>, the state <c>Synced</c> in one save.</item>
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
    /// <summary>Key of the reason of the "To do" of dates taken on the channel manager, in <c>EmailTexts</c> (Italian and English).</summary>
    internal const string DatesTakenReasonKey = "TodoReason_OctorateDatesTaken";

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
            return await StateOfAsync(bookingId, ct) ?? OctorateSyncState.Synced;

        // A booking of a channel is the channel's (it collects, it holds it), and one that came from Octorate is already there: no echo.
        if (FiscalCopy.IsOtaBookingSource(booking.Source)
            || booking.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn)
            || booking.CheckOutDate < timeProvider.TodayInRome())
        {
            return null;
        }

        // Before anything of this unit of work is changed: the lookup may save the only room of the mapping.
        var lookup = await targets.LookupAsync(booking.PropertyId, ct);
        if (lookup.Gap == OctorateTargetGap.NotMirrored)
            return null;

        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                db, PostgresAdvisoryLocks.Scope.OctorateBookingSync, bookingId.ToString("N"), ct)
            ?? throw new OctorateSyncBusyException(bookingId);

        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == bookingId && s.OrgId == booking.OrgId, ct);
        var reservationId = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => b.OctorateReservationId)
            .FirstOrDefaultAsync(ct);
        if (reservationId is not null)
            return sync?.State ?? OctorateSyncState.Synced;

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

        // The attempt is committed before the call: a process that stops during it leaves the trace that makes the next attempt look first.
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
                // No answer, a 429 or a 5xx: the reservation may have been created. It is looked up before anything else (D13).
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
                // A booking Octorate could not take as it is (no night, a reference too long): refused, not retried.
                await RecordFailureAsync(sync, OctorateSyncErrorCodes.Rejected, ct);
                logger.LogWarning(ex, "Octorate push of booking {BookingId}: the reservation cannot be written, not sent", bookingId);
                return OctorateSyncState.Failed;
            }
        }

        await RecordSyncedAsync(booking, sync, reservation, ct);
        logger.LogInformation(
            "Octorate push of booking {BookingId} of org {OrgId}: reservation {ReservationId} recorded (attempt {Attempt})",
            bookingId,
            booking.OrgId,
            reservation.Id,
            sync.Attempts);

        // The payments made before the reservation existed (their event found nothing to record them on): recorded now, under the same lock.
        await RegisterPaidPaymentsAsync(booking.OrgId, bookingId, target, reservation.Id, ct);
        return OctorateSyncState.Synced;
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
            .Select(b => new { b.Id, b.OrgId, b.PropertyId, b.Source, b.OctorateReservationId })
            .FirstOrDefaultAsync(ct);

        // A channel collects its own bookings: nothing of theirs is recorded on Octorate.
        if (booking is null || FiscalCopy.IsOtaBookingSource(booking.Source))
            return;

        var state = await StateOfAsync(booking.Id, ct);
        if (booking.OctorateReservationId is null)
        {
            // The reservation is being created: delivered again after the create, never dropped and never recorded twice.
            if (state == OctorateSyncState.Pending)
                throw new OctorateSyncPendingException(booking.Id);

            // Not mirrored, or its create failed: there is no reservation to record the payment on (a create that succeeds later records it).
            return;
        }

        // A reservation that came from Octorate (no sync row) or a mirror in error: CasaZen records nothing on it.
        if (state != OctorateSyncState.Synced)
            return;

        // A property no longer mirrored, or write-blocked, records nothing.
        var lookup = await targets.LookupAsync(booking.PropertyId, ct);
        if (lookup.Target is not { } target)
            return;

        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                db, PostgresAdvisoryLocks.Scope.OctorateBookingSync, booking.Id.ToString("N"), ct)
            ?? throw new OctorateSyncBusyException(booking.Id);
        await RegisterPaymentAsync(booking.OrgId, target, booking.OctorateReservationId, paymentId, ct);
    }

    /// <summary>
    /// Records one paid payment on the reservation, the caller holding the lock of the booking: read again (it may have been recorded meanwhile),
    /// looked for on the reservation first (a registration whose answer was lost is already there, D13), then sent; the transaction is kept on the
    /// payment, unique in the org.
    /// </summary>
    /// <exception cref="InvalidOperationException">The <c>paymentMode</c> of the way the payment was collected is not configured.</exception>
    private async Task RegisterPaymentAsync(Guid orgId, OctorateBookingTarget target, string reservationId, Guid paymentId, CancellationToken ct)
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

        using (orgScope.Use(orgId))
        {
            var reservation = await client.GetReservationAsync(target.AccommodationId, reservationId, ct);
            if (!reservation.PaymentTransactions.Contains(transaction, StringComparer.Ordinal))
            {
                await client.RegisterPaymentAsync(
                    target.AccommodationId,
                    reservationId,
                    new OctoratePaymentRegistration(transaction, mode, payment.Amount, payment.ProcessedAt ?? payment.UpdatedAt),
                    ct);
            }
        }

        payment.OctorateTransactionId = transaction;
        payment.OctorateRegisteredAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Octorate payment {PaymentId} of org {OrgId}: recorded on reservation {ReservationId} ({Mode})", paymentId, orgId, reservationId, mode);
    }

    /// <summary>The paid payments of a booking not recorded on Octorate yet; one that cannot be recorded now is left to its own event.</summary>
    private async Task RegisterPaidPaymentsAsync(
        Guid orgId,
        Guid bookingId,
        OctorateBookingTarget target,
        string reservationId,
        CancellationToken ct)
    {
        var paid = await db.Payments
            .AsNoTracking()
            .Where(p => p.OrgId == orgId
                        && p.BookingId == bookingId
                        && p.Status == PaymentStatus.Completed
                        && p.OctorateTransactionId == null)
            .Select(p => p.Id)
            .ToListAsync(ct);
        foreach (var paymentId in paid)
        {
            try
            {
                await RegisterPaymentAsync(orgId, target, reservationId, paymentId, ct);
            }
            catch (Exception ex) when (ex is OctorateException or InvalidOperationException or DbUpdateException)
            {
                logger.LogWarning(
                    "Octorate push of booking {BookingId}: payment {PaymentId} not recorded now ({Error})", bookingId, paymentId, ex.GetType().Name);
            }
        }
    }

    /// <summary>The <c>transaction</c> of a payment on Octorate: the Stripe PaymentIntent id, or <c>casazen-{paymentId}</c> for one the host recorded.</summary>
    internal static string TransactionOf(Payment payment) =>
        string.IsNullOrWhiteSpace(payment.StripePaymentIntentId) ? $"casazen-{payment.Id}" : payment.StripePaymentIntentId;

    /// <summary>The way a payment was collected, as the keys of <c>Octorate:PaymentModes</c> name it; <c>null</c> for one CasaZen does not record.</summary>
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

    public async Task<OctorateSyncState> RequeueAsync(Guid bookingId, bool overwrite, CancellationToken ct)
    {
        // Through the tenant filter of the caller: a booking of another org is not found (404).
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, ct)
            ?? throw new NotFoundException("Booking not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == bookingId && s.OrgId == booking.OrgId, ct);
        if (sync is null || sync.State is not (OctorateSyncState.Failed or OctorateSyncState.Conflict))
        {
            throw new DomainConflictException(
                OctorateSyncErrorCodes.NotRetryable, OctorateSyncErrorCodes.MessageKeys[OctorateSyncErrorCodes.NotRetryable]);
        }

        var now = UtcNow;
        sync.State = OctorateSyncState.Pending;
        sync.ErrorCode = null;
        sync.UpdatedAt = now;

        // Its own event, not a second BookingConfirmed: only the push runs again, no routine and no other consumer of the confirmation.
        recorder.Record(
            DomainEventType.OctorateSyncRequested,
            booking.Id,
            BookingEventPayload.Of(booking, BookingEventOrigin.System),
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

    /// <summary>What the push reads of a booking: ids, state, dates, guests, price, the booker's name and sex. Nothing is logged of it.</summary>
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
                b.Guest.Gender))
            .FirstOrDefaultAsync(ct);

    private async Task<OctorateSyncState?> StateOfAsync(Guid bookingId, CancellationToken ct) =>
        await db.OctorateBookingSyncs
            .AsNoTracking()
            .Where(s => s.BookingId == bookingId)
            .Select(s => (OctorateSyncState?)s.State)
            .FirstOrDefaultAsync(ct);

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

    /// <summary>Octorate refused the dates: a 409, or the 406 of "not available to book".</summary>
    private static bool IsDatesRefusal(OctorateException exception) =>
        exception is OctorateConflictException || exception.OctorateStatusCode == OctorateApiClient.NotAvailableStatus;

    /// <summary>The reservation of the booking found by its reference; <c>null</c> when it is not there or the lookup could not tell.</summary>
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

    private async Task RecordSyncedAsync(BookingFacts booking, OctorateBookingSync sync, OctorateReservation reservation, CancellationToken ct)
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
        await db.SaveChangesAsync(ct);
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

    /// <summary>What the push reads of a booking.</summary>
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
        Gender? Gender);
}

/// <summary>
/// Another push of the same booking holds its lock (E08/F01/US01/T03): this delivery leaves without writing anything and the outbox dispatcher
/// delivers the event again.
/// </summary>
public sealed class OctorateSyncBusyException(Guid bookingId)
    : Exception($"Booking {bookingId} is being sent to Octorate by another worker: delivered again later.");

/// <summary>
/// A payment of a booking whose reservation is still being created on Octorate (E08/F01/US01/T04): the event is delivered again after the
/// create, so the payment is never dropped and never recorded twice.
/// </summary>
public sealed class OctorateSyncPendingException(Guid bookingId)
    : Exception($"The reservation of booking {bookingId} is still being created on Octorate: the payment is delivered again later.");
