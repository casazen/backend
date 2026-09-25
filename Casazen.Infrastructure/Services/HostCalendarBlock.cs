using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A calendar block as the host calendar shows it (MO-06, A6-10): its nights [<paramref name="StartUtc"/>,
/// <paramref name="EndUtc"/>) as midnight UTC of their days, the SUMMARY of the event, and the channel and label of the
/// feed it was imported from. <paramref name="Channel"/> and <paramref name="FeedLabel"/> are null for a block that
/// does not come from a feed.
/// </summary>
/// <param name="Source">Imported from a feed or entered by hand.</param>
/// <param name="FeedId">Its import feed; null for a manual block.</param>
/// <param name="StayId">The OTA stay created from it (CO-21), while that stay is not cancelled.</param>
/// <param name="RepresentedByStay">
/// The stay takes exactly its nights (<see cref="Core.Services.PropertyOccupancy.IsRepresentedByStay"/>): the calendar
/// shows the stay only.
/// </param>
/// <param name="Convertible">"Crea soggiorno OTA" is offered (<see cref="Core.Services.OtaStays.IsConvertible"/>).</param>
public sealed record HostCalendarBlock(
    Guid Id,
    Guid PropertyId,
    DateTime StartUtc,
    DateTime EndUtc,
    string? Summary,
    ICalFeedChannel? Channel,
    string? FeedLabel,
    CalendarBlockSource Source = CalendarBlockSource.ICalImport,
    Guid? FeedId = null,
    Guid? StayId = null,
    bool RepresentedByStay = false,
    bool Convertible = false);
