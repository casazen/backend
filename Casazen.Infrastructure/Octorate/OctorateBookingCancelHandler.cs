using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// A booking CasaZen holds (a direct one, one the host entered) that is cancelled in CasaZen is cancelled on Octorate after it (E08/F02/US02/T02,
/// NF-76, AC-NF76-1): the consumer of <c>BookingCancelled</c>, recorded with the cancellation in the same save, so the policy preview and the
/// Stripe refund of <c>BookingCancellationService</c> have already been decided when this runs. <b>CasaZen first, Octorate after</b>: the refund of
/// the guest never waits for this push, and a failure of Octorate never undoes the cancellation. It is also the consumer of the host's «ask
/// again» (<c>OctorateSyncRequested</c>) for a cancellation whose push failed.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No echo.</b> A cancellation that came from Octorate (<see cref="BookingEventOrigin.Octorate"/>: the import, or the cancellation of a
/// channel booking that Octorate confirmed first) is never sent back, and a booking of a channel is never cancelled from here (it goes through
/// <see cref="IOtaBookingCancellationService"/>). Only what exists on Octorate: a booking with no reservation, and every event while
/// <c>Features:OctorateConnector</c> is off, send nothing.</item>
/// <item><b>Not mirrored, never silent.</b> A property that is not mirrored now (the connection was lost or is in error, the mapping is still to decide
/// or write-blocked) is never written to. When its booking already has its reservation on Octorate the cancellation is not dropped, though: the row
/// is <c>Failed</c> with <see cref="OctorateSyncErrorCodes.NotMirrored"/> (nothing was sent), so the host asks again once the connection or the mapping
/// is back, and nothing leaves a reservation alive on Octorate with no trace.</item>
/// <item><b>The state the host sees</b> is the <see cref="OctorateBookingSync"/> row (<see cref="OctorateSyncOperation.Cancel"/>), under the
/// session lock of the booking: <c>Pending</c> before the call (so a process that stops during it leaves a trace), then <c>Synced</c>. Octorate
/// refusing the dates or the state of the reservation (409): <c>Conflict</c>; another refusal (4xx): <c>Failed</c>; neither is tried again by
/// itself, the host asks again. No answer, 429 or 5xx: <c>Failed</c> and the exception propagates, so the dispatcher delivers the event again
/// (ADR-022-R4, R5). The call is idempotent (the client sends nothing for a reservation that is cancelled already), so a delivery that follows a
/// lost answer succeeds.</item>
/// <item><b>Out of order.</b> The events arrive in the order they occurred, not in the order they were handled: a cancellation that finds the
/// create of the reservation under way (its row is <c>Pending</c>) is delivered again after it, never consumed with no effect.</item>
/// <item>Ids, enums and dates only are read from the event (ADR-022-R21); the org is the event's (ADR-022-R6). Logs carry ids and codes.</item>
/// </list>
/// </remarks>
public sealed class OctorateBookingCancelHandler(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IOctorateBookingTargets targets,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<OctorateBookingCancelHandler> logger) : IEventHandler<DomainEvent>
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.BookingCancelled or DomainEventType.OctorateSyncRequested) || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        BookingEventPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BookingEventPayload>(domainEvent.PayloadJson, PayloadOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null)
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}) of org {OrgId} has a payload that is not a booking event: nothing is sent to Octorate",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return;
        }

        // A change that came from Octorate is never sent back, and a booking of a channel is cancelled through the channel path, Octorate first.
        if (payload.Origin == BookingEventOrigin.Octorate || FiscalCopy.IsOtaBookingSource(payload.Source))
            return;

        await PushAsync(domainEvent.OrgId, domainEvent.EntityId, domainEvent.Type == DomainEventType.OctorateSyncRequested, ct);
    }

    /// <summary>Cancels the reservation of a cancelled booking on Octorate, once and with a state the host sees (see the remarks of the class).</summary>
    private async Task PushAsync(Guid orgId, Guid bookingId, bool askedAgain, CancellationToken ct)
    {
        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                db, PostgresAdvisoryLocks.Scope.OctorateBookingSync, bookingId.ToString("N"), ct)
            ?? throw new OctorateSyncBusyException(bookingId);

        // Read under the lock, from what is committed: the create may have finished, the booking may have been brought back to life meanwhile.
        var booking = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .Select(b => new BookingFacts(b.Id, b.OrgId, b.PropertyId, b.Status, b.Source, b.OctorateReservationId))
            .FirstOrDefaultAsync(ct);
        if (booking is null)
        {
            logger.LogWarning("Octorate cancellation of booking {BookingId} of org {OrgId}: the booking is not found, nothing is sent", bookingId, orgId);
            return;
        }

        // The event is older than the booking (a hold confirmed again by a late payment): there is nothing to cancel. A booking of a channel is
        // cancelled through the channel path.
        if (booking.Status != BookingStatus.Cancelled || FiscalCopy.IsOtaBookingSource(booking.Source))
            return;

        var sync = await db.OctorateBookingSyncs.FirstOrDefaultAsync(s => s.BookingId == bookingId && s.OrgId == orgId, ct);

        // The host asks again: only for a cancellation that was sent (its row says so); the other rows are the business of the create.
        if (askedAgain && sync is not { LastOperation: OctorateSyncOperation.Cancel })
            return;

        if (booking.OctorateReservationId is not { } reservationId)
        {
            // No reservation on Octorate yet. When its create is under way (the row is Pending, or a process stopped during it) the event is delivered
            // again after it, never consumed with no effect. Otherwise the reservation was never made (the create gives up on a cancelled booking):
            // there is nothing to cancel.
            if (sync is { State: OctorateSyncState.Pending })
                throw new OctorateSyncPendingException(bookingId);

            return;
        }

        OctorateTargetLookup lookup;
        try
        {
            lookup = await targets.LookupAsync(booking.PropertyId, ct);
        }
        catch (OctorateException ex)
        {
            // The rooms of the accommodation are read on Octorate: a failed read leaves a visible row too, never a cancellation with no trace.
            var retry = ex is OctorateServerException or OctorateRateLimitException;
            await RecordAsync(booking, sync, OctorateSyncState.Failed, retry ? OctorateSyncErrorCodes.Unavailable : OctorateSyncErrorCodes.Rejected, ct);
            logger.LogWarning(
                "Octorate cancellation of booking {BookingId}: the target could not be read ({Code}, HTTP {Status})", bookingId, ex.Code, ex.OctorateStatusCode);
            if (retry)
                throw;

            return;
        }

        // Not mirrored (the connection was lost or is in error, a mapping still to decide or write-blocked): nothing is ever written to Octorate, but
        // the booking has its reservation there, so the cancellation is not consumed with no trace: the row says it was not sent and the host asks
        // again (the retry runs this again) once the connection or the mapping is back.
        if (lookup.Gap == OctorateTargetGap.NotMirrored)
        {
            await RecordAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.NotMirrored, ct);
            logger.LogWarning(
                "Octorate cancellation of booking {BookingId} of org {OrgId}: its property is not mirrored now, nothing is sent; the host asks again", bookingId, orgId);
            return;
        }

        if (lookup.Target is not { } target)
        {
            await RecordAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.RoomNotChosen, ct);
            return;
        }

        // The attempt is committed before the call, so a process that stops during it leaves the trace.
        sync = await MarkPendingAsync(booking, sync, ct);

        try
        {
            using (orgScope.Use(orgId))
                await client.CancelReservationAsync(target, reservationId, ct);
        }
        catch (OctorateConflictException)
        {
            // Octorate refuses the state of the reservation (409): the host looks at it, nothing is sent again by itself.
            await RecordAsync(booking, sync, OctorateSyncState.Conflict, OctorateSyncErrorCodes.ReservationChanged, ct);
            logger.LogWarning("Octorate cancellation of booking {BookingId}: the reservation conflicts with its state on Octorate, not retried", bookingId);
            return;
        }
        catch (OctorateException ex) when (ex is OctorateServerException or OctorateRateLimitException)
        {
            // No answer, a 429 or a 5xx: the cancellation may or may not have reached Octorate. The call is idempotent, so the event is delivered again.
            await RecordAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.Unavailable, ct);
            logger.LogWarning(
                "Octorate cancellation of booking {BookingId}: no answer ({Code}, HTTP {Status}); the event is delivered again", bookingId, ex.Code, ex.OctorateStatusCode);
            throw;
        }
        catch (OctorateException ex)
        {
            await RecordAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.Rejected, ct);
            logger.LogWarning(
                "Octorate cancellation of booking {BookingId}: refused by Octorate ({Code}, HTTP {Status}), not retried", bookingId, ex.Code, ex.OctorateStatusCode);
            return;
        }
        catch (ArgumentException ex)
        {
            // An id Octorate could not take: nothing left CasaZen, nothing was cancelled, and sending it again changes nothing.
            await RecordAsync(booking, sync, OctorateSyncState.Failed, OctorateSyncErrorCodes.Rejected, ct);
            logger.LogWarning(ex, "Octorate cancellation of booking {BookingId}: the request cannot be written, not sent", bookingId);
            return;
        }

        await RecordAsync(booking, sync, OctorateSyncState.Synced, null, ct);
        logger.LogInformation("Octorate cancellation of booking {BookingId} of org {OrgId}: the reservation is cancelled on Octorate", bookingId, orgId);
    }

    private async Task<OctorateBookingSync> MarkPendingAsync(BookingFacts booking, OctorateBookingSync? sync, CancellationToken ct)
    {
        var now = UtcNow;
        sync ??= NewSync(booking, now);
        sync.State = OctorateSyncState.Pending;
        sync.LastOperation = OctorateSyncOperation.Cancel;
        sync.PropertyId = booking.PropertyId;
        sync.Attempts++;
        sync.LastAttemptAt = now;
        sync.ErrorCode = null;
        sync.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return sync;
    }

    /// <summary>Writes the outcome of the cancellation on the row of the booking (made when the booking has none), in one save.</summary>
    private async Task RecordAsync(BookingFacts booking, OctorateBookingSync? sync, OctorateSyncState state, string? errorCode, CancellationToken ct)
    {
        var now = UtcNow;
        sync ??= NewSync(booking, now);
        sync.State = state;
        sync.LastOperation = OctorateSyncOperation.Cancel;
        sync.PropertyId = booking.PropertyId;
        sync.ErrorCode = errorCode;
        sync.UpdatedAt = now;
        if (state == OctorateSyncState.Synced)
            sync.LastSyncedAt = now;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Octorate cancellation of booking {BookingId}: {State} ({Code})", booking.Id, state, errorCode ?? "none");
    }

    private OctorateBookingSync NewSync(BookingFacts booking, DateTime now)
    {
        var sync = new OctorateBookingSync
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            PropertyId = booking.PropertyId,
            LastOperation = OctorateSyncOperation.Cancel,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OctorateBookingSyncs.Add(sync);
        return sync;
    }

    /// <summary>What the push reads of a booking: ids and enums only.</summary>
    private sealed record BookingFacts(
        Guid Id,
        Guid OrgId,
        Guid PropertyId,
        BookingStatus Status,
        BookingSource Source,
        string? OctorateReservationId);
}
