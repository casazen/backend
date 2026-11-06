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
    public static OtaChannel? Of(string? channelId, string? channelName) => Bookings.ToSource(channelId, channelName) switch
    {
        BookingSource.Airbnb => OtaChannel.Airbnb,
        BookingSource.BookingCom => OtaChannel.Booking,
        BookingSource.Vrbo => OtaChannel.Vrbo,
        BookingSource.Expedia => OtaChannel.Expedia,
        _ => null,
    };
}
