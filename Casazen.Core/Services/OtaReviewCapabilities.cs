using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// What the reviews of a booking can do, as one fact: read them, answer the guest's review, write the host's own review of the guest (E05/F06/US01/T02,
/// OCT-12, S6). <paramref name="Reason"/> says why nothing is allowed when the channel does not offer it, a stable code the screens turn into one sentence.
/// </summary>
public readonly record struct OtaReviewCapability(bool CanRead, bool CanReply, bool CanHostReview, string? Reason)
{
    /// <summary>Nothing can be done, for <paramref name="reason"/>.</summary>
    public static OtaReviewCapability Closed(string reason) => new(false, false, false, reason);
}

/// <summary>
/// The one table of what the reviews of each channel allow (E05/F06/US01/T02, OCT-12, S6): <b>pure</b>, closed by default, one place to change. The channel manager
/// integrates the reviews of Airbnb only: its API reads the guest's review and the host's reply of an Airbnb booking and writes the reply and the host's own review.
/// Booking.com, Vrbo and Expedia have no integration for reviews (S6): no read, no reply and no simulated row, with the reason
/// <see cref="ChannelNotIntegrated"/>; a fallback by hand is a product exception to approve, and it is not built here. The reviews of the booking site
/// (<see cref="Review"/>) are CasaZen's own: the host reads them and answers once, and writes no review of the guest.
/// </summary>
public static class OtaReviewCapabilities
{
    /// <summary>The channel has no integration for reviews: the channel manager neither reads nor writes them (S6). The sentence of the screens names the channel.</summary>
    public const string ChannelNotIntegrated = "channel_not_integrated";

    /// <summary>The booking comes from no channel and from no booking site (entered by the host, an iCal block): nobody reviews it.</summary>
    public const string NoReviewsForSource = "no_reviews_for_booking_source";

    /// <summary>The channels that have a row of the table, in the order of <see cref="OtaChannel"/>.</summary>
    public static IReadOnlyList<OtaChannel> Channels { get; } = [OtaChannel.Airbnb, OtaChannel.Booking, OtaChannel.Vrbo, OtaChannel.Expedia];

    /// <summary>What the reviews of <paramref name="channel"/> allow. A channel that is not in the table is closed, with <see cref="ChannelNotIntegrated"/>.</summary>
    public static OtaReviewCapability For(OtaChannel channel) => channel switch
    {
        OtaChannel.Airbnb => new OtaReviewCapability(true, true, true, null),
        _ => OtaReviewCapability.Closed(ChannelNotIntegrated),
    };

    /// <summary>The reviews of the booking site: read and answered by the host once, no review of the guest.</summary>
    public static OtaReviewCapability Direct { get; } = new(true, true, false, null);

    /// <summary>
    /// What the reviews of a booking allow, from where it came from: the booking site is <see cref="Direct"/>, a portal is its channel's row (the portals the channel
    /// manager has no chat for, Agoda and TripAdvisor, have no integration either), a booking of the host or of an iCal feed has no reviews at all.
    /// </summary>
    public static OtaReviewCapability OfSource(BookingSource source) => source switch
    {
        BookingSource.Direct => Direct,
        BookingSource.Airbnb => For(OtaChannel.Airbnb),
        BookingSource.BookingCom => For(OtaChannel.Booking),
        BookingSource.Vrbo => For(OtaChannel.Vrbo),
        BookingSource.Expedia => For(OtaChannel.Expedia),
        BookingSource.TripAdvisor or BookingSource.Agoda => OtaReviewCapability.Closed(ChannelNotIntegrated),
        _ => OtaReviewCapability.Closed(NoReviewsForSource),
    };
}
