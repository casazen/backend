using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IBookingService
{
    Task<Booking?> GetBookingAsync(Guid id);

    Task<IReadOnlyList<Booking>> GetBookingsAsync(
        HostScope scope,
        Guid? propertyId = null,
        Guid? guestId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a booking entered by the host: always <see cref="BookingStatus.Confirmed"/> with source
    /// <see cref="BookingSource.Manual"/>, stored together with its guest snapshot (same org as the booking).
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// The booking breaks a rule (<see cref="BookingErrorCodes.CreateInvalid"/>), or the property is in long-term mode
    /// (<see cref="PropertyRentalModeErrorCodes.NotBookableInLongMode"/>, PM-01).
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">The dates are taken (<see cref="BookingErrorCodes.DatesUnavailable"/>).</exception>
    Task<Booking> CreateManualBookingAsync(Booking booking, Guest guest);

    Task<Booking> UpdateBookingAsync(Booking booking);
    Task<bool> IsPropertyAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut, int? pendingDirectTtlMinutes = null);
    Task<IEnumerable<Booking>> GetCalendarAsync(Guid propertyId, DateTime startDate, DateTime endDate);
    /// <summary>
    /// Public checkout: a <see cref="BookingStatus.Pending"/> hold with its PaymentIntent / SetupIntent, or a "pay at the
    /// property" request (BK-06). The result carries the checkout token of the outcome page (BK-07), returned only here.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist or is not bookable.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 with a code of <see cref="DirectBookingErrorCodes"/>, <see cref="BookingErrorCodes.TooManyGuests"/> or
    /// <see cref="OnSiteRequestErrorCodes.TooManyNights"/>; <see cref="PropertyRentalModeErrorCodes.NotBookableInLongMode"/>
    /// when the property is in long-term mode (PM-01).
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
    /// <see cref="BookingErrorCodes.TooManyGuests"/>, <see cref="DirectBookingErrorCodes.InvalidStay"/> or
    /// <see cref="PropertyRentalModeErrorCodes.NotBookableInLongMode"/> (the property is in long-term mode, PM-01).
    /// </exception>
    Task<DirectBookingQuote> QuoteDirectBookingAsync(DirectBookingQuoteInput input, CancellationToken cancellationToken = default);

    Task<DirectBookingQuote> PriceHostStayAsync(
        Property property,
        DateTime checkInDate,
        DateTime checkOutDate,
        int numberOfAdults,
        int numberOfChildren,
        IReadOnlyList<int>? childrenAges,
        CancellationToken cancellationToken = default);
}