using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A calendar block as the host calendar shows it (MO-06, A6-10): its nights [<paramref name="StartUtc"/>,
/// <paramref name="EndUtc"/>) as midnight UTC of their days, the SUMMARY of the event, and the channel and label of the
/// feed it was imported from. <paramref name="Channel"/> and <paramref name="FeedLabel"/> are null for a block that
/// does not come from a feed.
/// </summary>
public sealed record HostCalendarBlock(
    Guid Id,
    Guid PropertyId,
    DateTime StartUtc,
    DateTime EndUtc,
    string? Summary,
    ICalFeedChannel? Channel,
    string? FeedLabel);
