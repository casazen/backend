using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The channel of a thread of the chat of Octorate (E05/F01/US01/T02): the channel (portal) code or name of its reservation, or of the
/// connection it comes through, to <see cref="OtaChannel"/>; <c>null</c> for every channel without an integrated chat (Agoda, TripAdvisor, the
/// booking engine of the agency, a code nobody knows). It does not keep a table of codes of its own: the codes and the names are the ones of
/// <see cref="BookingStateMapper"/>, the single definition of "which channel is this reservation from", so a booking and its conversation can
/// never disagree about their channel.
/// </summary>
public static class OtaChannelMapper
{
    private static readonly BookingStateMapper Bookings = new();

    /// <summary>The channel with an integrated chat that <paramref name="channelId"/> / <paramref name="channelName"/> name, or <c>null</c>.</summary>
    public static OtaChannel? Of(string? channelId, string? channelName) => Of(Bookings.ToSource(channelId, channelName));

    /// <summary>
    /// The channel with an integrated chat that a booking came from, or <c>null</c> for every other source (Agoda, TripAdvisor, another portal, the
    /// booking site, the host): the one definition of «a booking of a channel that has a chat», which the reply of the hub reads (E05/F02/US02/T01).
    /// </summary>
    public static OtaChannel? Of(BookingSource source) => source switch
    {
        BookingSource.Airbnb => OtaChannel.Airbnb,
        BookingSource.BookingCom => OtaChannel.Booking,
        BookingSource.Vrbo => OtaChannel.Vrbo,
        BookingSource.Expedia => OtaChannel.Expedia,
        _ => null,
    };
}
