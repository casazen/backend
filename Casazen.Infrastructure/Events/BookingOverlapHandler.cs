using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// A booking that arrives from the channel manager on nights that are already taken raises an urgent «To do» and an alert, without waiting for
/// the host to open the calendar (E08/F01/US02/T02, NF-80, AC-NF80-2, ADR-026, ADR-025). The «To do» is <see cref="ActivityType.BookingOverlap"/>,
/// about the booking, on its property, leading to the booking (<see cref="ActivityAction.ReviewBooking"/>); it closes by itself when the nights
/// are free again.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which events raise</b>: <c>BookingCreated</c>, <c>BookingConfirmed</c> and <c>BookingModified</c> whose payload says the origin is
/// the channel manager (<see cref="BookingEventOrigin.Octorate"/>, set by the inbound handlers). A booking made in CasaZen is refused on taken
/// dates by the checks that write it, so it never needs this.</item>
/// <item><b>One rule</b>: the conflicts are the ones of <see cref="CalendarAlertRules"/> that feed the host calendar and the Home, on the bookings
/// that take their dates (<see cref="CheckoutHolds.OccupiesDates"/>) and the calendar blocks (<see cref="PropertyOccupancy"/>), read for the
/// nights of the booking; there is no second implementation. A booking that takes no night of its own (cancelled, an expired hold) has no
/// conflict. The nights the channel manager holds closed (a block of source <see cref="CalendarBlockSource.Octorate"/>, written by the sync of its
/// calendar) are not counted: for a booking that came from it they are its own reservation, often written before the booking is imported, and
/// nothing else takes them.</item>
/// <item><b>One item for a booking</b>, whatever the number of conflicts or events: the reason names the property and the first range of nights
/// the booking shares, in the language of the org, with no name, contact or note of the guest (ADR-026-R3). It is <see cref="ActivityUrgency.High"/>
/// and due at the start of the first shared night in Rome. <see cref="IInternalActivityService.CreateAsync"/> returns the item that is open, so an
/// event delivered again, or another event for the same conflict, creates nothing. An item the host closed by hand for the same nights is not
/// raised again by a later event of the booking (a change of the price must not bring it back); nights that changed are another item.</item>
/// <item><b>The alert</b>: <see cref="AlertType.BookingNeedsReview"/>, <see cref="AlertUrgency.High"/>, to the people the host notifications go
/// to for a property (<c>HostNotificationAudience</c>: the member in charge while they reach it, and the administrators of the org), before the
/// item is created, so a delivery that stops in between is made again and never loses the alert. <see cref="IAlertRouter"/> writes none twice
/// while one is open.</item>
/// <item><b>Closing</b>: on <c>BookingCancelled</c> and <c>BookingModified</c>, of any origin, the open items of the property are checked again
/// and closed by the system when their booking no longer has a conflict (or no longer takes its nights). If the nights moved, the item is
/// replaced by one that says the new ones. The item the push to the channel manager raised for a booking of CasaZen whose dates the channel
/// manager refused (<see cref="OctorateSyncState.Conflict"/>) is left alone while the booking is alive: its conflict is on the other side, which
/// CasaZen's calendar cannot see.</item>
/// <item>Ids, enums and dates only are read from the event (ADR-022-R21); the org is the event's (ADR-022-R6) and every query names it. A failure
/// of the database is not swallowed: the event is delivered again.</item>
/// </list>
/// </remarks>
public sealed class BookingOverlapHandler(
    AppDbContext db,
    IInternalActivityService activities,
    IAlertRouter alertRouter,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<BookingOverlapHandler> logger) : IEventHandler<DomainEvent>
{
    /// <summary>Key of the reason of the «To do» in <see cref="EmailTexts"/> (Italian and English): the property and the nights.</summary>
    internal const string ReasonKey = "TodoReason_BookingOverlap";

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.BookingCreated
                or DomainEventType.BookingConfirmed
                or DomainEventType.BookingModified
                or DomainEventType.BookingCancelled)
            || domainEvent.EntityId == Guid.Empty)
        {
            return;
        }

        var raises = domainEvent.Type != DomainEventType.BookingCancelled && CameFromTheChannelManager(domainEvent);

        // First what a cancellation or a change may have freed on the property, then what the booking of the event brings.
        if (domainEvent.Type is DomainEventType.BookingCancelled or DomainEventType.BookingModified)
            await CheckOpenItemsOfThePropertyAsync(domainEvent, except: raises ? domainEvent.EntityId : null, ct);

        if (raises)
            await ReconcileAsync(domainEvent.OrgId, domainEvent.EntityId, mayRaise: true, ct);
    }

    /// <summary>The payload of the event says the booking was made or changed by the channel manager.</summary>
    private bool CameFromTheChannelManager(DomainEvent domainEvent)
    {
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
                "Event {EventId} ({EventType}) of org {OrgId} has a payload that is not a booking event: no check of the nights taken",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return false;
        }

        return payload.Origin == BookingEventOrigin.Octorate;
    }

    /// <summary>
    /// A booking was cancelled or changed: every open item of its property is looked at again, because the nights it frees may be the ones
    /// another booking was waiting for. <paramref name="except"/> is the booking the event itself will reconcile.
    /// </summary>
    private async Task CheckOpenItemsOfThePropertyAsync(DomainEvent domainEvent, Guid? except, CancellationToken ct)
    {
        var orgId = domainEvent.OrgId;
        var bookingId = domainEvent.EntityId;
        var propertyId = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .Select(b => (Guid?)b.PropertyId)
            .FirstOrDefaultAsync(ct);
        if (propertyId is not { } property)
            return;

        var typeName = nameof(ActivityType.BookingOverlap);
        var waiting = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == typeName
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.PropertyId == property
                        && a.EntityId != null)
            .Select(a => a.EntityId!.Value)
            .ToListAsync(ct);

        foreach (var waitingBookingId in waiting.Distinct().Where(id => id != except))
            await ReconcileAsync(orgId, waitingBookingId, mayRaise: false, ct);
    }

    /// <summary>
    /// Brings the item of a booking in line with its conflicts now: closed when it has none, replaced when the nights moved, raised (with its
    /// alert) when <paramref name="mayRaise"/> and there is none yet.
    /// </summary>
    private async Task ReconcileAsync(Guid orgId, Guid bookingId, bool mayRaise, CancellationToken ct)
    {
        var open = await OpenItemAsync(orgId, bookingId, ct);
        if (open is null && !mayRaise)
            return;

        var stay = await ReadStayAsync(orgId, bookingId, ct);
        var occupies = false;
        IReadOnlyList<CalendarAlert> conflicts = [];
        if (stay is not null)
            (occupies, conflicts) = await ConflictsAsync(stay, ct);

        var first = conflicts.Count > 0 ? conflicts[0] : null;

        if (open is not null)
        {
            // The dates the channel manager refused for a booking of CasaZen: the overlap is over there, not in this calendar.
            if (occupies && await PushRefusedAsync(orgId, bookingId, ct))
                return;

            if (stay is null || first is null)
            {
                await activities.CloseAsync(open.Id, InternalActivity.ClosedBySystem, ct);
                logger.LogInformation("Booking {BookingId} of org {OrgId} has no nights taken any more: its To do is closed", bookingId, orgId);
                return;
            }

            var wanted = await DescribeAsync(stay, first, ct);
            if (OneLine(open.Reason) == wanted.Reason && open.Deadline == wanted.Deadline)
                return;

            // The nights moved: the item says the old ones. Another item for the new nights; the alert already told the host about the booking.
            await activities.CloseAsync(open.Id, InternalActivity.ClosedBySystem, ct);
            await RaiseItemAsync(stay, wanted, ct);
            return;
        }

        if (stay is null || first is null)
            return;

        var item = await DescribeAsync(stay, first, ct);
        if (await ClosedByAPersonAsync(orgId, bookingId, item, ct))
            return;

        // The alert goes first and is written once while open, the item last and once while open: a delivery that stops between the two is made
        // again, and what is left to do is whatever is missing.
        await AlertAsync(stay, item, ct);
        await RaiseItemAsync(stay, item, ct);
    }

    /// <summary>The open item of the booking, if it has one.</summary>
    private Task<OpenItem?> OpenItemAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var typeName = nameof(ActivityType.BookingOverlap);
        return db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == typeName
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId == bookingId)
            .Select(a => new OpenItem(a.Id, a.Reason, a.Deadline))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// A person closed an item of the booking for these same nights: the host has seen it and a later event of the booking (the price changed)
    /// does not bring it back. An item the system closed (the nights were free) does not count: if the conflict comes back, so does the item.
    /// </summary>
    private Task<bool> ClosedByAPersonAsync(Guid orgId, Guid bookingId, Described item, CancellationToken ct)
    {
        var typeName = nameof(ActivityType.BookingOverlap);
        var reason = item.Reason;
        var deadline = item.Deadline;
        return db.InternalActivities
            .AsNoTracking()
            .AnyAsync(
                a => a.OrgId == orgId
                     && a.Action != null
                     && a.CompletedAt != null
                     && a.ActivityType == typeName
                     && a.EntityType == InternalActivity.BookingEntityType
                     && a.EntityId == bookingId
                     && a.ClosedBy != InternalActivity.ClosedBySystem
                     && a.Reason == reason
                     && a.Deadline == deadline,
                ct);
    }

    /// <summary>The push of the booking to the channel manager found its dates taken there (E08/F01/US01/T03): that item is the sync's.</summary>
    private Task<bool> PushRefusedAsync(Guid orgId, Guid bookingId, CancellationToken ct) =>
        db.OctorateBookingSyncs
            .AsNoTracking()
            .AnyAsync(s => s.OrgId == orgId && s.BookingId == bookingId && s.State == OctorateSyncState.Conflict, ct);

    /// <summary>The booking and its property, in the org of the event; <c>null</c> when either is gone.</summary>
    private async Task<StayFacts?> ReadStayAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .Select(b => new { b.PropertyId, b.CheckInDate, b.CheckOutDate })
            .FirstOrDefaultAsync(ct);
        if (booking is null)
        {
            logger.LogWarning("Booking {BookingId} of org {OrgId} not found, no nights to check", bookingId, orgId);
            return null;
        }

        var property = await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == booking.PropertyId && p.OrgId == orgId)
            .Select(p => new { p.Name, p.OwnerId, p.ResponsibleUserId })
            .FirstOrDefaultAsync(ct);
        if (property is null)
        {
            logger.LogInformation("Property {PropertyId} of booking {BookingId} not found, no nights to check", booking.PropertyId, bookingId);
            return null;
        }

        return new StayFacts(
            orgId, bookingId, booking.PropertyId, property.Name, property.OwnerId, property.ResponsibleUserId, booking.CheckInDate, booking.CheckOutDate);
    }

    /// <summary>
    /// The conflicts of the booking with what else takes its nights, by <see cref="CalendarAlertRules"/>, the earliest first, and whether the
    /// booking takes its own nights at all (a cancelled booking, an expired hold, does not).
    /// </summary>
    private async Task<(bool Occupies, IReadOnlyList<CalendarAlert> Conflicts)> ConflictsAsync(StayFacts stay, CancellationToken ct)
    {
        var cutoff = CheckoutHolds.CutoffAt(timeProvider.GetUtcNow().UtcDateTime, CheckoutHolds.GetTtlMinutes(configuration));

        var bookings = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == stay.OrgId)
            .Where(PropertyOccupancy.BookingTakesNightIn(stay.PropertyId, stay.CheckIn, stay.CheckOut))
            .Where(CheckoutHolds.OccupiesDates(cutoff))
            .Select(b => new { b.Id, b.CheckInDate, b.CheckOutDate })
            .ToListAsync(ct);
        if (bookings.All(b => b.Id != stay.BookingId))
            return (false, []);

        var blocks = await db.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.OrgId == stay.OrgId)
            // The nights the channel manager holds closed are its view of its own reservations, the one that has just arrived among them (the sync
            // of its calendar writes them every 15 minutes, before or after the booking is imported): nothing else takes those nights, and the item
            // raised for them would stay open when the next sync removes the block, because only a booking event closes it.
            .Where(b => b.Source != CalendarBlockSource.Octorate)
            .Where(PropertyOccupancy.BlockTakesNightIn(stay.PropertyId, stay.CheckIn, stay.CheckOut))
            .Select(b => new { b.Id, b.Source, b.StartUtc, b.EndUtc })
            .ToListAsync(ct);

        var occupants = bookings
            .Select(b => CalendarAlertRules.StayOccupant(b.Id, stay.PropertyId, b.CheckInDate, b.CheckOutDate))
            .Concat(blocks.Select(b => CalendarAlertRules.BlockOccupant(b.Id, stay.PropertyId, b.Source, b.StartUtc, b.EndUtc)))
            .ToList();

        var conflicts = CalendarAlertRules.ConflictsOfStay(
            timeProvider.TodayInRomeAsDateOnly(),
            new CalendarAlertProperty(stay.PropertyId, stay.PropertyName),
            stay.BookingId,
            DateOnly.FromDateTime(stay.CheckIn),
            DateOnly.FromDateTime(stay.CheckOut),
            occupants);
        return (true, conflicts);
    }

    /// <summary>The reason and the deadline of the item for the first range of shared nights, in the language of the org.</summary>
    private async Task<Described> DescribeAsync(StayFacts stay, CalendarAlert first, CancellationToken ct)
    {
        var language = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == stay.OrgId)
            .Select(o => o.DefaultLanguage)
            .FirstOrDefaultAsync(ct);

        return new Described(ReasonOf(OrgInvitationEmails.CultureOf(language), stay.PropertyName, first), RomeCalendar.StartOfDayUtc(first.From));
    }

    /// <summary>
    /// «{property}: the booking arrives on nights that are already taken, from {first night} to {the day they are free again}»: the property and
    /// the nights, nothing of the guest. On one line and at most <see cref="TodoLimits.ReasonMaxLength"/> characters, as the list keeps it.
    /// </summary>
    internal static string ReasonOf(CultureInfo culture, string propertyName, CalendarAlert conflict)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(conflict);

        // DateOnly takes the formats of a date only; both are the date formats of the texts of the emails.
        var dateFormat = EmailTexts.Get("Format_Date", culture);
        var reason = OneLine(
            string.Format(
                culture,
                EmailTexts.Get(ReasonKey, culture),
                propertyName,
                conflict.From.ToString(dateFormat, culture),
                conflict.To.ToString(dateFormat, culture)));
        return reason.Length <= TodoLimits.ReasonMaxLength ? reason : reason[..TodoLimits.ReasonMaxLength].TrimEnd();
    }

    /// <summary>The alert of the booking to the people who reach its property, once for each person while it is open.</summary>
    private async Task AlertAsync(StayFacts stay, Described item, CancellationToken ct)
    {
        var users = await HostNotificationAudience
            .UsersToTell(db, stay.OrgId, stay.PropertyId, stay.ResponsibleUserId, stay.OwnerId)
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var userId in users.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await alertRouter.RouteAlertAsync(
                AlertType.BookingNeedsReview,
                stay.BookingId,
                PopupAlert.BookingEntityType,
                userId,
                AlertUrgency.High,
                item.Deadline,
                ct);
        }
    }

    private async Task RaiseItemAsync(StayFacts stay, Described item, CancellationToken ct)
    {
        try
        {
            await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.BookingOverlap,
                    Action = ActivityAction.ReviewBooking,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = stay.BookingId,
                    Urgency = ActivityUrgency.High,
                    PropertyId = stay.PropertyId,
                    Deadline = item.Deadline,
                    Reason = item.Reason,
                },
                ct);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nothing to review, and a retry would find the same.
            logger.LogInformation("Property {PropertyId} of booking {BookingId} not found, no To do for the nights taken", stay.PropertyId, stay.BookingId);
        }
    }

    /// <summary>The same one-line form the list keeps a reason in (<see cref="IInternalActivityService.CreateAsync"/>), so the two compare.</summary>
    private static string OneLine(string? value) =>
        value is null ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record OpenItem(Guid Id, string? Reason, DateTime? Deadline);

    private sealed record Described(string Reason, DateTime Deadline);

    private sealed record StayFacts(
        Guid OrgId,
        Guid BookingId,
        Guid PropertyId,
        string PropertyName,
        string OwnerId,
        string? ResponsibleUserId,
        DateTime CheckIn,
        DateTime CheckOut);
}
