using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Events;

/// <summary>
/// The payload of <see cref="DomainEventType.ReviewReceived"/> (E26/F01/US01/T01, ADR-022-R21): which review, which stay and which property, ids only.
/// Never the mark, the text, the name of the guest or anything else the guest wrote: a consumer reads the review from the table, in its own org, by
/// <c>EntityId</c>. <see cref="BookingId"/> is the name the routines read to find the facts of the stay (the conditions of a routine on this event).
/// </summary>
/// <param name="ReviewId">The review the guest sent.</param>
/// <param name="BookingId">The stay it is about.</param>
/// <param name="PropertyId">Its property.</param>
public sealed record ReviewReceivedPayload(Guid ReviewId, Guid BookingId, Guid PropertyId)
{
    /// <summary>
    /// The dedup key of the event of <paramref name="reviewId"/>: one event for each review, ever. The event is written in the same save as the review, which is
    /// one for each booking, so the key is a second wall (the unique index of the outbox refuses a second event with it).
    /// </summary>
    public static string DedupKeyOf(Guid reviewId) => $"review-received:{reviewId:N}";
}
