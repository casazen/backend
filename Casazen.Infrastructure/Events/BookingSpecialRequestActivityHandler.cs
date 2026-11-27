using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// A booking came with a special request of the guest: raises the "To do" «Nuova prenotazione con una richiesta speciale»
/// (<see cref="ActivityType.BookingSpecialRequest"/>, E08/F06/US01/T02, NF-79, AC-NF79-4), about the booking, on its property, leading to
/// the booking (<see cref="ActivityAction.ReviewBooking"/>). It closes at the check-in or when the booking is cancelled
/// (<see cref="ActivityAutoCloseHandler"/>), or when the host closes it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which events</b>: <c>BookingCreated</c> and <c>BookingConfirmed</c>. A booking made confirmed (entered by the host, from a
/// channel) has both, one made by a guest has the first when the hold is created and the second when it is paid or accepted. The
/// item is raised for a booking that is confirmed when the event is handled, not for a hold that may never be paid or a request the
/// host has not answered, which would raise and close an item for nothing.</item>
/// <item><b>The request</b> is read from the booking, in the database, by id: the payload of the event carries no text. Only whether
/// it is empty matters; what it says is never copied, logged or put in the item (ADR-026-R3). The reason is a fixed sentence in the
/// language of the org.</item>
/// <item><b>Once</b>: one item per booking, ever, for the two events and for their redelivery (ADR-022-R7), also after the item was
/// closed by hand.</item>
/// <item>The item is <see cref="ActivityUrgency.Medium"/> (it does not count on the badge of the urgent ones) and due at the start of
/// the check-in day in Rome. The org is the event's (ADR-022-R6).</item>
/// </list>
/// </remarks>
public sealed class BookingSpecialRequestActivityHandler(
    AppDbContext db,
    IInternalActivityService activities,
    ILogger<BookingSpecialRequestActivityHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.BookingCreated or DomainEventType.BookingConfirmed) || domainEvent.EntityId == Guid.Empty)
            return;

        var orgId = domainEvent.OrgId;
        var bookingId = domainEvent.EntityId;
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .Select(b => new { b.PropertyId, b.Status, b.CheckInDate, b.SpecialRequests })
            .FirstOrDefaultAsync(ct);
        if (booking is null)
        {
            logger.LogWarning("Event {EventId}: booking {BookingId} of org {OrgId} not found, no special request to read", domainEvent.Id, bookingId, orgId);
            return;
        }

        if (booking.Status != BookingStatus.Confirmed || string.IsNullOrWhiteSpace(booking.SpecialRequests))
            return;

        if (await BookingTodoItems.WasRaisedAsync(db, orgId, ActivityType.BookingSpecialRequest, bookingId, ct))
            return;

        var request = new CreateTodoRequest
        {
            Type = ActivityType.BookingSpecialRequest,
            Action = ActivityAction.ReviewBooking,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = bookingId,
            Urgency = ActivityUrgency.Medium,
            PropertyId = booking.PropertyId,
            Deadline = RomeCalendar.StartOfDayUtc(booking.CheckInDate),
            Reason = await BookingTodoItems.ReasonAsync(db, orgId, BookingTodoItems.SpecialRequestReason, ct),
        };

        try
        {
            await activities.CreateAsync(request, ct);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nothing to read, and a retry would find the same.
            logger.LogInformation("Event {EventId}: property {PropertyId} of booking {BookingId} not found, no special request to read", domainEvent.Id, booking.PropertyId, bookingId);
        }
    }
}
