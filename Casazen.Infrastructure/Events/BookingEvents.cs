using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// How a flow of CasaZen records the lifecycle event of a booking it makes or changes (E08/F06/US01/T01, ADR-022-R1): through
/// <see cref="IDomainEventRecorder"/>, on the same <see cref="AppDbContext"/> and in the same unit of work as the change, so the
/// <c>SaveChangesAsync</c> (and the transaction) that commits the change commits the event, and a change that is refused or rolled
/// back leaves no event. Nothing is published here: the outbox dispatcher does it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>When</b>: right after the booking is changed in memory and before the save that writes it, never after it (an event after
/// the commit could be lost with the process, ADR-022-R1). A flow that does not save in the same unit (the repository saves a new
/// booking itself) records before it calls the repository, which withdraws the events of a booking it refuses
/// (<see cref="Withdraw"/>).</item>
/// <item><b>Once</b>: the dedup key (<see cref="DedupKeyOf"/>, ADR-022-R8) names the transition. A transition that happened before
/// (a hold that expired and was confirmed again by a late payment, then cancelled) finds its key taken: the flow is never failed for
/// it. The event of a confirmation or a cancellation that comes back takes the key with the instant of the change; any other event
/// of a key already taken is not recorded again (a repeated save, a retried request).</item>
/// <item><b>What</b>: <see cref="BookingEventPayload"/>, ids, enums and dates only. The org is the booking's, passed explicitly: the
/// payment webhook and the jobs have no request org.</item>
/// <item>The recorder is optional in the services that take it, so the tests that build them by hand keep working; the container
/// always provides it. Without it nothing is recorded.</item>
/// </list>
/// </remarks>
public static class BookingEvents
{
    /// <summary>The dedup key of the event of a transition of <paramref name="booking"/> (the keys of the brief E08/F06/US01/T01).</summary>
    public static string DedupKeyOf(DomainEventType type, Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);

        return type switch
        {
            DomainEventType.BookingCreated => $"booking-created:{booking.Id}",
            DomainEventType.BookingConfirmed => $"booking-confirmed:{booking.Id}",
            DomainEventType.BookingCancelled => $"booking-cancelled:{booking.Id}",
            DomainEventType.BookingModified => $"booking-modified:{booking.Id}:{booking.UpdatedAt:O}",
            DomainEventType.CheckInCompleted => $"checkin:{booking.Id}",
            DomainEventType.CheckOutCompleted => $"checkout:{booking.Id}",
            DomainEventType.PropertyReadyDeclared => $"property-ready:{booking.Id}",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an event of the lifecycle of a booking."),
        };
    }

    /// <summary>
    /// Records the events of a booking that is being created, not saved yet: <c>BookingCreated</c>, and <c>BookingConfirmed</c> when
    /// it is created confirmed (a booking entered by the host, a stay made from a channel's block). A booking is created once, so its
    /// keys cannot be taken.
    /// </summary>
    public static void RecordCreated(IDomainEventRecorder? recorder, Booking booking, BookingEventOrigin origin)
    {
        if (recorder is null)
            return;

        Add(recorder, DomainEventType.BookingCreated, booking, origin, DedupKeyOf(DomainEventType.BookingCreated, booking));
        if (booking.Status == BookingStatus.Confirmed)
            Add(recorder, DomainEventType.BookingConfirmed, booking, origin, DedupKeyOf(DomainEventType.BookingConfirmed, booking));
    }

    /// <summary>
    /// Records the event of a transition of a booking that exists, not saved yet, once (see the remarks of the class). The booking is
    /// as it is after the change: its status, its dates and its <c>UpdatedAt</c>.
    /// </summary>
    public static async Task RecordAsync(
        IDomainEventRecorder? recorder,
        AppDbContext db,
        DomainEventType type,
        Booking booking,
        BookingEventOrigin origin,
        CancellationToken cancellationToken = default)
    {
        if (recorder is null)
            return;

        ArgumentNullException.ThrowIfNull(db);
        var key = DedupKeyOf(type, booking);
        if (await IsTakenAsync(db, booking.OrgId, key, cancellationToken))
        {
            // Only a confirmation or a cancellation can come back for the same booking: that one is a new event.
            if (type is not (DomainEventType.BookingConfirmed or DomainEventType.BookingCancelled))
                return;

            key = $"{key}:{booking.UpdatedAt:O}";
            if (await IsTakenAsync(db, booking.OrgId, key, cancellationToken))
                return;
        }

        Add(recorder, type, booking, origin, key);
    }

    /// <summary>
    /// Takes back the events recorded for <paramref name="bookingId"/> in the current unit of work that are not saved: the change they
    /// describe was refused, so no later save of the same context may write them (ADR-022-R1).
    /// </summary>
    public static void Withdraw(AppDbContext db, Guid bookingId)
    {
        ArgumentNullException.ThrowIfNull(db);

        foreach (var entry in db.ChangeTracker.Entries<OutboxEvent>()
                     .Where(e => e.State == EntityState.Added && e.Entity.EntityId == bookingId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static void Add(IDomainEventRecorder recorder, DomainEventType type, Booking booking, BookingEventOrigin origin, string key) =>
        recorder.Record(type, booking.Id, BookingEventPayload.Of(booking, origin), key, booking.OrgId);

    /// <summary>The key is in the outbox of the org already, or in the events of this unit of work.</summary>
    private static async Task<bool> IsTakenAsync(AppDbContext db, Guid orgId, string key, CancellationToken cancellationToken)
    {
        if (db.ChangeTracker.Entries<OutboxEvent>().Any(e =>
                e.State == EntityState.Added && e.Entity.OrgId == orgId && e.Entity.DedupKey == key))
        {
            return true;
        }

        // The org is named, so the tenant filter of the caller (a job, a webhook, an admin) does not decide what is taken.
        return await db.OutboxEvents
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(e => e.OrgId == orgId && e.DedupKey == key, cancellationToken);
    }
}
