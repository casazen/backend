using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>What the host gives to turn an iCal block into an OTA stay (CO-21). Names and email are required.</summary>
/// <param name="NumberOfGuests">Guests of the stay; <see cref="OtaStays.DefaultNumberOfGuests"/> when not given.</param>
/// <param name="TotalPrice">Amount of the reservation as the channel shows it, when the host gives it; never made up.</param>
/// <param name="Source">OTA of the reservation, used only when the feed's channel is "other".</param>
public sealed record OtaStayConversion(
    Guid BlockId,
    string FirstName,
    string LastName,
    string Email,
    int? NumberOfGuests = null,
    decimal? TotalPrice = null,
    BookingSource? Source = null);

/// <summary>OTA stays created by the host from iCal blocks (CO-21, decision D7). Rules in <see cref="OtaStays"/>.</summary>
public interface IOtaStayService
{
    /// <summary>The calendar block, or null when it does not exist or belongs to another org (tenant filter). Read only.</summary>
    Task<CalendarBlock?> FindBlockAsync(Guid blockId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the stay of the block: a <see cref="BookingStatus.Confirmed"/> booking with the OTA source of the feed, the
    /// block's dates, a new guest of the property's org, and the block linked to it. Under the lock of the property's iCal
    /// feeds (a sync never removes the block meanwhile) and of its dates (no other booking takes them meanwhile).
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="OtaStayErrorCodes.BlockNotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <see cref="OtaStayErrorCodes.AlreadyConverted"/>, <see cref="OtaStayErrorCodes.OverlapsBooking"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="OtaStayErrorCodes.BlockNotImported"/>, <see cref="OtaStayErrorCodes.BlockEnded"/>,
    /// <see cref="OtaStayErrorCodes.SourceRequired"/>, <see cref="BookingErrorCodes.TooManyGuests"/>,
    /// <see cref="BookingErrorCodes.CreateInvalid"/>.
    /// </exception>
    Task<Booking> ConvertBlockAsync(OtaStayConversion conversion, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host checked the reservation on the channel: the stay is no longer "da verificare". With
    /// <paramref name="applyChannelDates"/> the stay first takes the dates its block now has on the channel (only a
    /// confirmed stay whose arrival is not registered; the other bookings are checked as for any change of dates). Nothing
    /// else changes; a stay with nothing to check is returned as it is.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">No such booking (or of another org).</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="OtaStayErrorCodes.ChannelDatesUnavailable"/> (no linked block), <see cref="BookingErrorCodes.StayLocked"/>,
    /// <see cref="BookingErrorCodes.NotEditable"/>, <see cref="BookingErrorCodes.CheckInInPast"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="BookingErrorCodes.DatesUnavailable"/>.</exception>
    Task<Booking> ResolveReviewAsync(Guid bookingId, bool applyChannelDates = false, CancellationToken cancellationToken = default);

    /// <summary>The imported block linked to the stay, or null (not a stay from iCal, or its block left the feed). Read only.</summary>
    Task<CalendarBlock?> GetLinkedBlockAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
