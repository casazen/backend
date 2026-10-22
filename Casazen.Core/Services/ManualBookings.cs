using Casazen.Core.Entities;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// The booking the host enters by hand with only what it needs (SR-06a, <c>POST /api/bookings/manual</c>): the name and the
/// email of the guest, the stay and, when the host agreed another price, that price with a short note. The price and the tourist
/// tax come from the quote of the host (<see cref="IBookingService.PriceHostStayAsync"/>), never from the client.
/// </summary>
public static class ManualBookingRules
{
    /// <summary>Longest note on the price the host agreed, in characters.</summary>
    public const int PriceNoteMaxLength = 80;

    /// <summary>
    /// Highest lodging price (cleaning included, tourist tax excluded) the host may agree: <see cref="Booking.BasePrice"/> is the
    /// figure the Home and the fiscal reports read, so a typing mistake must not reach them.
    /// </summary>
    public const decimal MaxAgreedBasePrice = 100_000m;

    /// <summary>
    /// The price a manual booking records. Without an agreed total, or with the total of the quote, it is the quote. With another
    /// total the guest pays that total, the tourist tax stays the one of the quote and the lodging is what is left of the total:
    /// <c>BasePrice = total − tax</c>, between 0 and <see cref="MaxAgreedBasePrice"/>. The cleaning fee of the quote is kept as far as
    /// it fits in the lodging, so that <c>BasePrice − CleaningFee</c> is never negative. The note is kept only with an agreed price.
    /// </summary>
    /// <exception cref="DomainRuleException">
    /// <see cref="BookingErrorCodes.PriceOverrideInvalid"/>: the lodging is negative or above the cap, or the note is too long.
    /// </exception>
    public static ManualBookingPrice PriceOf(DirectBookingQuote quote, decimal? agreedTotal, string? note)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var trimmedNote = note?.Trim();
        if (trimmedNote is { Length: > PriceNoteMaxLength })
            throw new DomainRuleException(BookingErrorCodes.PriceOverrideInvalid, "BookingPriceNoteTooLong");

        var tax = quote.TouristTax.AmountOrZero;
        var quoted = new ManualBookingPrice(quote.BasePrice, quote.CleaningFee, tax, quote.TotalPrice, Overridden: false, Note: null);
        if (agreedTotal is not { } agreed)
            return quoted;

        var total = decimal.Round(agreed, 2, MidpointRounding.AwayFromZero);
        if (total == decimal.Round(quote.TotalPrice, 2, MidpointRounding.AwayFromZero))
            return quoted;

        var basePrice = total - tax;
        if (basePrice < 0m || basePrice > MaxAgreedBasePrice)
            throw new DomainRuleException(BookingErrorCodes.PriceOverrideInvalid, "BookingPriceOverrideInvalid");

        return new ManualBookingPrice(
            basePrice,
            Math.Min(quote.CleaningFee, basePrice),
            tax,
            total,
            Overridden: true,
            Note: string.IsNullOrEmpty(trimmedNote) ? null : trimmedNote);
    }
}

/// <summary>What a manual booking records as its price (<see cref="ManualBookingRules.PriceOf"/>).</summary>
/// <param name="BasePrice">Lodging plus cleaning, tourist tax excluded (<see cref="Booking.BasePrice"/>).</param>
/// <param name="CleaningFee">The cleaning fee inside <paramref name="BasePrice"/>.</param>
/// <param name="TouristTax">The tourist tax of the quote, whatever the agreed total.</param>
/// <param name="TotalPrice">What the guest pays: <c>BasePrice + TouristTax</c>.</param>
/// <param name="Overridden">The host agreed a total other than the quote's.</param>
/// <param name="Note">The host's note on that price; null without an agreed price.</param>
public sealed record ManualBookingPrice(
    decimal BasePrice,
    decimal CleaningFee,
    decimal TouristTax,
    decimal TotalPrice,
    bool Overridden,
    string? Note);

/// <summary>The guest of a manual booking: the first name and the email are required, the rest is what the host knows.</summary>
public sealed record ManualBookingGuest(string FirstName, string? LastName, string Email, string? Phone, string? Country);

/// <summary>
/// What the host gives to enter a booking by hand. The stay is <paramref name="CheckInDate"/> to <paramref name="CheckOutDate"/>
/// (dates, the departure day is not a night). <paramref name="BlockId"/> completes the reservation a portal sent as a block of its
/// calendar instead of creating a new one: then the dates are the block's and <paramref name="TotalPrice"/> is the amount of the
/// portal, which collects it.
/// </summary>
/// <param name="Property">The property, read inside the caller's org and already authorized for <c>booking.write</c>.</param>
/// <param name="ChildrenAges">Age at check-in of each minor, asked when the tourist tax of the comune depends on it.</param>
/// <param name="Note">The host's note on the stay (<see cref="Booking.SpecialRequests"/>).</param>
/// <param name="TotalPrice">The total the host agreed, when it is not the quote's.</param>
/// <param name="PriceNote">The host's note on that price (<see cref="ManualBookingRules.PriceNoteMaxLength"/> characters at most).</param>
/// <param name="BlockId">The block of a portal's calendar the booking completes; null for a booking by direct contact.</param>
/// <param name="Source">The portal of the block, only when the feed of the block is of a channel that is not Airbnb or Booking.com.</param>
public sealed record ManualBookingInput(
    Property Property,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int Adults,
    int Children,
    IReadOnlyList<int>? ChildrenAges,
    ManualBookingGuest Guest,
    string? Note,
    decimal? TotalPrice,
    string? PriceNote,
    Guid? BlockId,
    BookingSource? Source);

/// <summary>A manual booking, created, and whether its stay is shorter than the minimum stay of the property.</summary>
/// <param name="BelowMinimum">The stay is shorter than <paramref name="MinNights"/>: allowed, the host enters it, but it is said.</param>
/// <param name="MinNights">The minimum stay of the property, when it has one.</param>
public sealed record ManualBookingResult(Booking Booking, bool BelowMinimum, int? MinNights);

/// <summary>
/// The booking the host enters by hand with only what it needs (SR-06a). A direct-contact booking is created
/// <see cref="BookingStatus.Confirmed"/> with the source <see cref="BookingSource.Manual"/>, priced by the quote of the host; a
/// booking that completes a portal's block becomes the OTA stay of that block (<see cref="IOtaStayService"/>), with no price made up
/// and no payment: the portal collects. Nothing is sent to the guest.
/// </summary>
public interface IManualBookingService
{
    /// <summary>Creates the booking; the final check on the dates runs inside the transaction that writes it.</summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422: a long-term property (<see cref="PropertyRentalModeErrorCodes.NotBookableInLongMode"/>), too many guests, a check-in in the past,
    /// the ages of the minors missing, an agreed price that cannot be recorded (<see cref="BookingErrorCodes.PriceOverrideInvalid"/>),
    /// dates that are not the block's (<see cref="BookingErrorCodes.BlockDatesMismatch"/>) and the rules of <see cref="OtaStays"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// 409 <see cref="BookingErrorCodes.DatesUnavailable"/>, or <see cref="OtaStayErrorCodes.AlreadyConverted"/> /
    /// <see cref="OtaStayErrorCodes.OverlapsBooking"/> for a block.
    /// </exception>
    /// <exception cref="Exceptions.NotFoundException">The block does not exist, is of another org or of another property (404).</exception>
    Task<ManualBookingResult> CreateAsync(ManualBookingInput input, CancellationToken cancellationToken = default);
}
