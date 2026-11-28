using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public readonly record struct OtaReviewCapability(bool CanRead, bool CanReply, bool CanHostReview, string? Reason)
{
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
    public const string ChannelNotIntegrated = "channel_not_integrated";

    public const string NoReviewsForSource = "no_reviews_for_booking_source";

    public static IReadOnlyList<OtaChannel> Channels { get; } = [OtaChannel.Airbnb, OtaChannel.Booking, OtaChannel.Vrbo, OtaChannel.Expedia];

    public static OtaReviewCapability For(OtaChannel channel) => channel switch
    {
        OtaChannel.Airbnb => new OtaReviewCapability(true, true, true, null),
        _ => OtaReviewCapability.Closed(ChannelNotIntegrated),
    };

    public static OtaReviewCapability Direct { get; } = new(true, true, false, null);

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
