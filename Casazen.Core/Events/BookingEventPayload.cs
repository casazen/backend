using System.Text.Json.Serialization;
using Casazen.Core.Entities;

namespace Casazen.Core.Events;

/// <summary>
/// Where a change of a booking comes from (E08/F06/US01/T01): the payload of every booking event says it, so that a consumer can tell
/// the changes made in CasaZen from the ones that arrived from the channel manager. The Octorate push never sends back a change whose
/// origin is <see cref="Octorate"/> (no echo of an inbound change). Stored by name in the payload.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookingEventOrigin
{
    /// <summary>The host, in CasaZen (web, app): a booking entered by hand, an edit, a confirmation, a cancellation, a check-in or check-out.</summary>
    Host = 1,

    /// <summary>The guest: a checkout of the booking site, a payment, a cancellation with the guest's link or from the guest area.</summary>
    Guest = 2,

    /// <summary>CasaZen itself: an expired hold, a request declined, a deferred payment that was not completed, a payment that arrived when the dates were taken.</summary>
    System = 3,

    /// <summary>The channel manager (Octorate): set by its inbound handlers, never by a flow of CasaZen.</summary>
    Octorate = 4,
}

/// <summary>
/// The payload of a booking event (<c>BookingCreated</c>, <c>BookingConfirmed</c>, <c>BookingModified</c>, <c>BookingCancelled</c>,
/// <c>CheckInCompleted</c>, <c>CheckOutCompleted</c>, <c>PropertyReadyDeclared</c>): ids, enums and dates only (ADR-022-R21, ADR-026-R3).
/// </summary>
/// <remarks>
/// Never the guest (name, contact), the notes or the special request, the prices or the payments: a consumer reads them from the
/// booking by <c>EntityId</c>, in its own org. The enums are written by name, so a stored event stays readable if a member is added. The
/// dates are the stay's, as the calendar knows them (Europe/Rome dates). <see cref="Status"/> is the status of the booking after the
/// change the event records.
/// </remarks>
public sealed record BookingEventPayload(
    Guid BookingId,
    Guid PropertyId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BookingSource Source,
    BookingEventOrigin Origin,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BookingStatus Status,
    DateOnly CheckIn,
    DateOnly CheckOut)
{
    /// <summary>
    /// Only on <c>OctorateSyncRequested</c> (E08/F02/US01/T02): the host chose to write CasaZen's version of a booking over the one that changed on
    /// Octorate («keep mine», <c>POST /api/bookings/{id}/octorate-sync/retry?overwrite=true</c>). A flag, not data; left out of the JSON when false,
    /// so the other events keep their shape.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Overwrite { get; init; }

    /// <summary>The payload of an event about <paramref name="booking"/> as it is now, made by <paramref name="origin"/>.</summary>
    public static BookingEventPayload Of(Booking booking, BookingEventOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(booking);

        return new BookingEventPayload(
            booking.Id,
            booking.PropertyId,
            booking.Source,
            origin,
            booking.Status,
            DateOnly.FromDateTime(booking.CheckInDate),
            DateOnly.FromDateTime(booking.CheckOutDate));
    }
}
