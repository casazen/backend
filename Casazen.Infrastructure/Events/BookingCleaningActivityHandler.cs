using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// A stay ended and nobody asked for a cleaning: raises the "To do" «Check-out completato senza una pulizia richiesta»
/// (<see cref="ActivityType.CleaningDue"/>, E08/F06/US01/T02, NF-79), about the booking that ended, on its property, due at the next
/// arrival of the property. It leads to the check-out wizard at its "property ready" step (<see cref="ActivityAction.CompleteCheckOut"/>)
/// and closes when the host declares the property ready (<see cref="DomainEventType.PropertyReadyDeclared"/>, by
/// <see cref="ActivityAutoCloseHandler"/>) or closes it by hand.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>When there is nothing to do</b>: the property was declared ready already (at the check-out wizard, or before the event
/// was delivered), or a cleaning request of the stay exists (not one that was declined or cancelled, which cleans nothing).</item>
/// <item><b>Urgency</b>: <see cref="ActivityUrgency.High"/> when the next check-in of the property is within 48 hours,
/// <see cref="ActivityUrgency.Medium"/> when it is later, <see cref="ActivityUrgency.Low"/> with no arrival booked. The next arrival is
/// the next confirmed booking of the property from the day of the check-out (a same-day turnover counts); the deadline is the start of
/// its check-in day in Rome.</item>
/// <item><b>Once</b>: one item per stay, ever. A redelivery of the event (at least once, ADR-022-R7) raises nothing, also after the
/// item was closed by hand or by the property being declared ready.</item>
/// <item><b>No personal data</b> (ADR-026-R3): the reason is a fixed sentence in the language of the org; the item is about the booking
/// by id and the screens read the rest from the booking.</item>
/// <item>The org is the event's (ADR-022-R6): the bus runs the handler under it and every query names it too.</item>
/// </list>
/// </remarks>
public sealed class BookingCleaningActivityHandler(
    AppDbContext db,
    IInternalActivityService activities,
    TimeProvider timeProvider,
    ILogger<BookingCleaningActivityHandler> logger) : IEventHandler<DomainEvent>
{
    /// <summary>A next arrival within this time makes the cleaning urgent.</summary>
    internal static readonly TimeSpan UrgentWithin = TimeSpan.FromHours(48);

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.CheckOutCompleted || domainEvent.EntityId == Guid.Empty)
            return;

        var orgId = domainEvent.OrgId;
        var bookingId = domainEvent.EntityId;
        var stay = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .Select(b => new { b.PropertyId, b.CheckOutDate })
            .FirstOrDefaultAsync(ct);
        if (stay is null)
        {
            logger.LogWarning("Event {EventId}: booking {BookingId} of org {OrgId} not found, no cleaning to do", domainEvent.Id, bookingId, orgId);
            return;
        }

        if (await BookingTodoItems.WasRaisedAsync(db, orgId, ActivityType.CleaningDue, bookingId, ct))
            return;

        var propertyReady = await db.StayCheckouts
            .AsNoTracking()
            .AnyAsync(c => c.OrgId == orgId && c.BookingId == bookingId && c.PropertyReadyAt != null, ct);
        if (propertyReady)
            return;

        var cleaningRequested = await db.ServiceRequests
            .AsNoTracking()
            .AnyAsync(
                r => r.OrgId == orgId
                     && r.BookingId == bookingId
                     && r.Category == ServiceCategories.Cleaning
                     && r.Status != ServiceRequestStatus.Annullato
                     && r.Status != ServiceRequestStatus.Rifiutato,
                ct);
        if (cleaningRequested)
            return;

        var checkOutDay = stay.CheckOutDate.Date;
        var nextCheckIn = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId
                        && b.PropertyId == stay.PropertyId
                        && b.Id != bookingId
                        && b.Status == BookingStatus.Confirmed
                        && b.CheckInDate >= checkOutDay)
            .OrderBy(b => b.CheckInDate)
            .Select(b => (DateTime?)b.CheckInDate)
            .FirstOrDefaultAsync(ct);

        DateTime? deadline = nextCheckIn is { } arrival ? RomeCalendar.StartOfDayUtc(arrival) : null;
        var request = new CreateTodoRequest
        {
            Type = ActivityType.CleaningDue,
            Action = ActivityAction.CompleteCheckOut,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = bookingId,
            Urgency = UrgencyOf(deadline, timeProvider.GetUtcNow().UtcDateTime),
            PropertyId = stay.PropertyId,
            Deadline = deadline,
            Reason = await BookingTodoItems.ReasonAsync(db, orgId, BookingTodoItems.CleaningDueReason, ct),
        };

        try
        {
            await activities.CreateAsync(request, ct);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nothing to clean, and a retry would find the same.
            logger.LogInformation("Event {EventId}: property {PropertyId} of booking {BookingId} not found, no cleaning to do", domainEvent.Id, stay.PropertyId, bookingId);
        }
    }

    /// <summary>High within <see cref="UrgentWithin"/> of <paramref name="now"/> (or already past), Medium later, Low with no arrival booked.</summary>
    internal static ActivityUrgency UrgencyOf(DateTime? deadline, DateTime now) =>
        deadline is not { } due
            ? ActivityUrgency.Low
            : due - now <= UrgentWithin ? ActivityUrgency.High : ActivityUrgency.Medium;
}
