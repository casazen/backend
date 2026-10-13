namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Why an OTA stay the host created from an iCal block (CO-21, decision D7) is "da verificare": a later sync of its feed
/// found the reservation changed on the channel. CasaZen never changes nor cancels the stay by itself: the host checks the
/// reservation on the channel, acts on the stay and marks it verified. Stored as an integer on
/// <see cref="Booking.OtaReviewReason"/>.
/// </summary>
public enum OtaStayReviewReason
{
    /// <summary>The block is gone from the feed while the stay was not over (reservation cancelled or moved on the channel).</summary>
    BlockRemoved = 1,

    /// <summary>The block now has other dates on the channel than the stay.</summary>
    BlockDatesChanged = 2,
}
