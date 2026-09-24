using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IBookingService
{
    Task<IEnumerable<Booking>> GetAllBookingsAsync();
    Task<Booking?> GetBookingAsync(Guid id);
    Task<IEnumerable<Booking>> GetPropertyBookingsAsync(Guid propertyId);
    Task<IEnumerable<Booking>> GetGuestBookingsAsync(Guid guestId);

    /// <summary>
    /// Creates a booking entered by the host: always <see cref="BookingStatus.Confirmed"/> with source
    /// <see cref="BookingSource.Manual"/>, stored together with its guest snapshot (same org as the booking).
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">The booking breaks a rule (<see cref="BookingErrorCodes.CreateInvalid"/>).</exception>
    /// <exception cref="Exceptions.DomainConflictException">The dates are taken (<see cref="BookingErrorCodes.DatesUnavailable"/>).</exception>
    Task<Booking> CreateManualBookingAsync(Booking booking, Guest guest);

    Task<Booking> UpdateBookingAsync(Booking booking);
    // Cancellation (with refunds and intents on Stripe) is IBookingCancellationService (BK-02).
    Task<bool> IsPropertyAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut, int? pendingDirectTtlMinutes = null);
    Task<IEnumerable<Booking>> GetCalendarAsync(Guid propertyId, DateTime startDate, DateTime endDate);
    /// <summary>
    /// Public checkout: a <see cref="BookingStatus.Pending"/> hold with its PaymentIntent / SetupIntent, or a "pay at the
    /// property" request (BK-06). The result carries the checkout token of the outcome page (BK-07), returned only here.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist or is not bookable.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 with a code of <see cref="DirectBookingErrorCodes"/>, <see cref="BookingErrorCodes.TooManyGuests"/> or
    /// <see cref="OnSiteRequestErrorCodes.TooManyNights"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// 409 <see cref="BookingErrorCodes.DatesUnavailable"/> or <see cref="DirectBookingErrorCodes.PaymentsNotReady"/>.
    /// </exception>
    /// <exception cref="Exceptions.PaymentProcessingException">Stripe could not create the intent (503).</exception>
    Task<DirectBookingCreateResult> CreateDirectBookingAsync(DirectBookingCreateInput input);

    /// <summary>
    /// Price of a direct booking before it is created (checkout, BK-03): the same calculation
    /// <see cref="CreateDirectBookingAsync"/> records, tourist tax included, and the payment options of the stay (A3-16).
    /// It does not check availability.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist or is not bookable.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="BookingErrorCodes.TooManyGuests"/> or <see cref="DirectBookingErrorCodes.InvalidStay"/>.
    /// </exception>
    Task<DirectBookingQuote> QuoteDirectBookingAsync(DirectBookingQuoteInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Price of a stay the host enters or changes in <paramref name="property"/> (manual booking, PC-07): the same
    /// calculation as <see cref="QuoteDirectBookingAsync"/> (nightly rate x nights + cleaning fee, tourist tax of BK-03),
    /// without the checks of the public booking site (active listing, compliance). It does not check availability.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="BookingErrorCodes.TooManyGuests"/> or <see cref="BookingErrorCodes.InvalidDates"/>.
    /// </exception>
    Task<DirectBookingQuote> PriceHostStayAsync(
        Property property,
        DateTime checkInDate,
        DateTime checkOutDate,
        int numberOfAdults,
        int numberOfChildren,
        IReadOnlyList<int>? childrenAges,
        CancellationToken cancellationToken = default);
}