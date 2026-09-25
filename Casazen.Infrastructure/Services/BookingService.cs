using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

public class BookingService(
    IBookingRepository repository,
    IPropertyRepository propertyRepository,
    IOrgService orgService,
    IGuestRepository guestRepository,
    ITouristTaxQuoteService touristTaxQuoteService,
    IStripeService stripeService,
    IPaymentRepository paymentRepository,
    PropertyICalSyncService propertyICalSyncService,
    IConfiguration configuration,
    ILogger<BookingService> logger,
    ICheckoutHoldExpiryService checkoutHoldExpiry,
    OnSiteRequestNotifier onSiteNotifier,
    TimeProvider? timeProvider = null) : IBookingService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<Booking?> GetBookingAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Booking>> GetPropertyBookingsAsync(Guid propertyId)
    {
        return await repository.GetByPropertyAsync(propertyId);
    }

    public async Task<IEnumerable<Booking>> GetGuestBookingsAsync(Guid guestId)
    {
        return await repository.GetByGuestAsync(guestId);
    }

    public async Task<IEnumerable<Booking>> GetAllBookingsAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<Booking> CreateManualBookingAsync(Booking booking, Guest guest)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(guest);
        if (booking.OrgId == Guid.Empty)
            throw new ArgumentException("A manual booking must carry the org of its property.", nameof(booking));

        // A booking entered by the host is a confirmed stay from the start: it occupies its dates on the booking site
        // and in the iCal export, and the checkout hold expiry never cancels it (PC-01, A2-01).
        booking.Status = BookingStatus.Confirmed;
        booking.Source = BookingSource.Manual;
        booking.GuestId = guest.Id;
        guest.OrgId = booking.OrgId;

        var validationResult = BookingValidator.ValidateBooking(booking, today: _clock.TodayInRome());
        if (!validationResult.IsValid)
        {
            logger.LogWarning(
                "Manual booking validation failed for property {PropertyId}: {Errors}",
                booking.PropertyId, validationResult.ErrorMessage);
            throw new DomainRuleException(BookingErrorCodes.CreateInvalid, "BookingCreateInvalid");
        }

        if (!await IsPropertyAvailableAsync(booking.PropertyId, booking.CheckInDate, booking.CheckOutDate))
        {
            logger.LogInformation(
                "Manual booking rejected: property {PropertyId} not available from {CheckIn:yyyy-MM-dd} to {CheckOut:yyyy-MM-dd}",
                booking.PropertyId, booking.CheckInDate, booking.CheckOutDate);
            throw new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
        }

        // The guest snapshot is written only once the dates are known to be free, and removed again if a concurrent
        // booking takes them before the insert (the repository re-checks under the property lock).
        await guestRepository.AddAsync(guest);
        try
        {
            logger.LogInformation("Creating manual booking for property {PropertyId}", booking.PropertyId);
            return await repository.AddAsync(booking);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Property not available", StringComparison.OrdinalIgnoreCase))
        {
            await guestRepository.DeleteAsync(guest.Id);
            throw new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
        }
    }

    public async Task<DirectBookingCreateResult> CreateDirectBookingAsync(DirectBookingCreateInput input)
    {
        // Every checkout error is a ProblemDetails with a stable code and a localized message (R-11, BK-07): the guest
        // reads why the booking failed instead of a generic "checkout failed".
        if (!Enum.IsDefined(input.PaymentOption))
        {
            throw new DomainRuleException(
                DirectBookingErrorCodes.InvalidPaymentOption, "DirectBookingInvalidPaymentOption");
        }

        var allowedConsentVersion = configuration["DirectBooking:ConsentVersion"] ?? "2026-06-direct-checkout-v1";
        if (!string.Equals(input.ConsentVersion, allowedConsentVersion, StringComparison.Ordinal))
        {
            throw new DomainRuleException(DirectBookingErrorCodes.ConsentOutdated, "DirectBookingConsentOutdated");
        }

        var property = await GetBookablePropertyAsync(input.PropertyId);

        var org = await orgService.GetByIdAsync(property.OrgId);
        if (org is null ||
            string.IsNullOrWhiteSpace(org.StripeConnectedAccountId) ||
            !org.ConnectChargesEnabled)
        {
            throw new DomainConflictException(DirectBookingErrorCodes.PaymentsNotReady, "DirectBookingPaymentsNotReady");
        }

        var totalGuests = input.NumberOfAdults + input.NumberOfChildren;
        var (checkIn, checkOut) = ValidateStay(property, input.CheckInDate, input.CheckOutDate, totalGuests);

        // A3-06: a "pay at the property" request holds its dates with no guarantee: its length is capped.
        if (input.PaymentOption == PaymentOption.OnSite)
        {
            var maxNights = OnSiteRequests.GetMaxNights(configuration);
            if ((checkOut - checkIn).Days > maxNights)
                throw new DomainRuleException(OnSiteRequestErrorCodes.TooManyNights, "OnSiteRequestTooManyNights", maxNights);
        }

        // Same price as the checkout quote (BK-03, R-05): computed before any write, so a missing age leaves nothing behind.
        var price = await PriceStayAsync(
            property, checkIn, checkOut, input.NumberOfAdults, input.NumberOfChildren, input.ChildrenAges);
        if (price.TouristTax.Status == TouristTaxQuoteStatus.ChildAgesRequired)
        {
            throw new DomainRuleException(DirectBookingErrorCodes.ChildAgesRequired, "TouristTaxChildAgesRequired");
        }

        // A3-16: "Paga alla scadenza" only when its charge day is still ahead (the checkout offers it from the same quote).
        if (input.PaymentOption == PaymentOption.OnCancellationDeadline && !price.PaymentOptions.DeferredPaymentAvailable)
        {
            throw new DomainRuleException(
                DirectBookingErrorCodes.DeferredPaymentUnavailable, "DirectBookingDeferredPaymentUnavailable");
        }

        var pendingTtlMinutes = GetPendingDirectTtlMinutes();

        if (!await IsPropertyAvailableAsync(input.PropertyId, checkIn, checkOut, pendingTtlMinutes))
        {
            throw new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
        }

        var guest = await CreateGuestSnapshotWithConsentAsync(
            property.OrgId, input.Guest, input.ConsentVersion, input.ConsentIpAddress);

        var basePrice = price.BasePrice;
        var touristTaxAmount = price.TouristTax.AmountOrZero;
        var totalPrice = price.TotalPrice;
        var currency = price.Currency;
        var freeRefundDeadline = price.FreeRefundDeadline;
        // Only the guest who made the checkout reads its outcome and resumes its payment (BK-07): the token is returned
        // once, the database keeps its hash.
        var checkoutToken = CheckoutOutcomes.NewToken();

        var booking = new Booking
        {
            PropertyId = input.PropertyId,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfAdults = input.NumberOfAdults,
            NumberOfChildren = input.NumberOfChildren,
            NumberOfGuests = totalGuests,
            SpecialRequests = input.SpecialRequests ?? string.Empty,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            BasePrice = basePrice,
            CleaningFee = price.CleaningFee,
            TouristTax = touristTaxAmount,
            TouristTaxAmount = touristTaxAmount,
            TotalPrice = totalPrice,
            PaymentOption = input.PaymentOption,
            FreeRefundDeadline = freeRefundDeadline,
            CheckoutTokenHash = CheckoutOutcomes.HashToken(checkoutToken),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // D5 (BK-06): a "pay at the property" request is never confirmed here. It holds its dates until the guest confirms
        // the email (link in the "request received" email), then until the host answers (OnSiteRequests).
        string? onSiteEmailToken = null;
        if (input.PaymentOption == PaymentOption.OnSite)
        {
            onSiteEmailToken = OnSiteRequests.NewEmailVerificationToken();
            booking.GuestEmailVerificationTokenHash = OnSiteRequests.HashEmailVerificationToken(onSiteEmailToken);
            booking.RequestExpiresAt = _clock.GetUtcNow().UtcDateTime
                .AddMinutes(OnSiteRequests.GetEmailVerificationMinutes(configuration));
        }

        var validationResult = BookingValidator.ValidateBooking(booking, today: _clock.TodayInRome());
        if (!validationResult.IsValid)
        {
            logger.LogInformation(
                "Direct booking validation failed for property {PropertyId}: {Errors}",
                input.PropertyId,
                validationResult.ErrorMessage);
            throw new DomainRuleException(DirectBookingErrorCodes.InvalidStay, "DirectBookingInvalidStay");
        }

        Booking createdBooking;
        try
        {
            createdBooking = await repository.AddAsync(booking);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Property not available", StringComparison.OrdinalIgnoreCase))
        {
            await guestRepository.DeleteAsync(guest.Id);
            throw new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
        }

        var amountCents = (long)Math.Round(totalPrice * 100m, MidpointRounding.AwayFromZero);
        var metadata = new Dictionary<string, string>
        {
            ["bookingId"] = createdBooking.Id.ToString(),
            ["propertyId"] = input.PropertyId.ToString(),
            ["orgId"] = property.OrgId.ToString(),
            ["kind"] = "direct-booking",
        };

        string? clientSecret = null;
        string? setupIntentClientSecret = null;

        switch (input.PaymentOption)
        {
            case PaymentOption.Immediate:
                clientSecret = await HandleImmediatePaymentAsync(
                    createdBooking, org.StripeConnectedAccountId!, amountCents, currency, metadata);
                break;

            case PaymentOption.OnCancellationDeadline:
                setupIntentClientSecret = await HandleDeferredPaymentAsync(
                    createdBooking, org.StripeConnectedAccountId!, guest, amountCents, currency, metadata);
                break;

            case PaymentOption.OnSite:
                await OpenOnSiteRequestAsync(createdBooking, onSiteEmailToken!);
                break;
        }

        var publishableKey = configuration["Stripe:PublishableKey"] ?? string.Empty;
        return new DirectBookingCreateResult(
            createdBooking.Id,
            clientSecret ?? string.Empty,
            publishableKey,
            org.StripeConnectedAccountId!,
            totalPrice,
            currency,
            touristTaxAmount,
            basePrice,
            setupIntentClientSecret,
            freeRefundDeadline,
            input.PaymentOption,
            price.TouristTax.Status,
            createdBooking.RequestExpiresAt,
            checkoutToken);
    }

    public async Task<DirectBookingQuote> QuoteDirectBookingAsync(
        DirectBookingQuoteInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var property = await GetBookablePropertyAsync(input.PropertyId);

        var (checkIn, checkOut) = ValidateStay(
            property, input.CheckInDate, input.CheckOutDate, input.NumberOfAdults + input.NumberOfChildren);

        return await PriceStayAsync(
            property, checkIn, checkOut, input.NumberOfAdults, input.NumberOfChildren, input.ChildrenAges,
            cancellationToken);
    }

    /// <summary>An active, unpaused property whose compliance allows public bookings; 404 otherwise (PC-03, A2-05).</summary>
    private async Task<Property> GetBookablePropertyAsync(Guid propertyId)
    {
        var property = await propertyRepository.GetByIdAsync(propertyId);
        if (property is null || !property.IsActive || property.IsPaused || property.ComplianceStatus != PropertyComplianceStatus.Active)
            throw new NotFoundException("Property not bookable") { MessageKey = "PropertyNotFound" };

        return property;
    }

    public async Task<DirectBookingQuote> PriceHostStayAsync(
        Property property,
        DateTime checkInDate,
        DateTime checkOutDate,
        int numberOfAdults,
        int numberOfChildren,
        IReadOnlyList<int>? childrenAges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (numberOfAdults + numberOfChildren > property.MaxGuests)
            throw new DomainRuleException(BookingErrorCodes.TooManyGuests, "BookingTooManyGuests", property.MaxGuests);

        var checkIn = checkInDate.Date;
        var checkOut = checkOutDate.Date;
        if (checkOut <= checkIn || (checkOut - checkIn).Days > TouristTaxCalculator.MaxNights)
            throw new DomainRuleException(BookingErrorCodes.InvalidDates, "BookingInvalidDates");

        return await PriceStayAsync(
            property, checkIn, checkOut, numberOfAdults, numberOfChildren, childrenAges, cancellationToken);
    }

    /// <summary>Guests within the capacity and a stay of 1 to <see cref="TouristTaxCalculator.MaxNights"/> nights.</summary>
    private static (DateTime CheckIn, DateTime CheckOut) ValidateStay(
        Property property,
        DateTime checkInDate,
        DateTime checkOutDate,
        int totalGuests)
    {
        if (totalGuests > property.MaxGuests)
        {
            throw new DomainRuleException(BookingErrorCodes.TooManyGuests, "BookingTooManyGuests", property.MaxGuests);
        }

        var checkIn = DateTime.SpecifyKind(checkInDate.Date, DateTimeKind.Utc);
        var checkOut = DateTime.SpecifyKind(checkOutDate.Date, DateTimeKind.Utc);
        if (checkOut <= checkIn || (checkOut - checkIn).Days > TouristTaxCalculator.MaxNights)
        {
            throw new DomainRuleException(DirectBookingErrorCodes.InvalidStay, "DirectBookingInvalidStay");
        }

        return (checkIn, checkOut);
    }

    /// <summary>
    /// Nightly rate x nights + cleaning fee, plus the tourist tax of <see cref="ITouristTaxQuoteService"/> when it can
    /// be calculated. The property has no accommodation category (yet): only the rates for every accommodation of the
    /// comune apply. The night price for percentage rates is the nightly rate, cleaning excluded. The payment options come
    /// from the free refund deadline of the stay (<see cref="DirectBookingPaymentRules"/>, A3-16).
    /// </summary>
    private async Task<DirectBookingQuote> PriceStayAsync(
        Property property,
        DateTime checkIn,
        DateTime checkOut,
        int adults,
        int children,
        IReadOnlyList<int>? childrenAges,
        CancellationToken cancellationToken = default)
    {
        var nights = (checkOut - checkIn).Days;
        var basePrice = property.NightlyRate * nights + property.CleaningFee;
        var touristTax = await touristTaxQuoteService.QuoteAsync(
            TouristTaxComune.ForProperty(property),
            new TouristTaxStay(
                RomeCalendar.DateInRome(checkIn),
                RomeCalendar.DateInRome(checkOut),
                adults,
                children,
                childrenAges,
                AccommodationCategory: null,
                NightlyPrice: property.NightlyRate),
            cancellationToken);

        var freeRefundDeadline = DirectBookingPaymentRules.FreeRefundDeadline(checkIn, property.CancellationPolicy);

        return new DirectBookingQuote(
            property.Id,
            checkIn,
            checkOut,
            nights,
            property.NightlyRate,
            property.CleaningFee,
            basePrice,
            touristTax,
            basePrice + touristTax.AmountOrZero,
            "EUR",
            freeRefundDeadline,
            DirectBookingPaymentRules.OptionsFor(freeRefundDeadline, _clock.TodayInRome()));
    }

    private async Task<string> HandleImmediatePaymentAsync(
        Booking booking,
        string stripeConnectedAccountId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata)
    {
        PaymentIntent paymentIntent;
        try
        {
            paymentIntent = await stripeService.CreateConnectedAccountPaymentIntentAsync(
                stripeConnectedAccountId,
                amountCents,
                currency.ToLowerInvariant(),
                metadata);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe PaymentIntent creation failed for booking {BookingId}", booking.Id);
            booking.Status = BookingStatus.Cancelled;
            booking.UpdatedAt = DateTime.UtcNow;
            await repository.UpdateAsync(booking);
            throw new PaymentProcessingException("Payment initialization failed", ex);
        }

        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = Core.Entities.PaymentMethod.CreditCard,
            TransactionId = paymentIntent.Id,
            StripePaymentIntentId = paymentIntent.Id,
            StripeAccountId = stripeConnectedAccountId,
            Description = "Direct checkout - immediate payment",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await paymentRepository.AddAsync(payment);

        return paymentIntent.ClientSecret ?? string.Empty;
    }

    private async Task<string> HandleDeferredPaymentAsync(
        Booking booking,
        string stripeConnectedAccountId,
        Guest guest,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata)
    {
        var setupMetadata = new Dictionary<string, string>(metadata)
        {
            ["kind"] = "direct-booking-setup",
        };

        SetupIntent setupIntent;
        try
        {
            setupIntent = await stripeService.CreateConnectedAccountSetupIntentAsync(
                stripeConnectedAccountId,
                setupMetadata,
                guest.Email,
                $"{guest.FirstName} {guest.LastName}".Trim());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe SetupIntent creation failed for booking {BookingId}", booking.Id);
            booking.Status = BookingStatus.Cancelled;
            booking.UpdatedAt = DateTime.UtcNow;
            await repository.UpdateAsync(booking);
            throw new PaymentProcessingException("Payment initialization failed", ex);
        }

        booking.StripeSetupIntentId = setupIntent.Id;
        booking.StripeCustomerId = setupIntent.CustomerId;
        booking.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(booking);

        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = Core.Entities.PaymentMethod.CreditCard,
            TransactionId = setupIntent.Id,
            StripeAccountId = stripeConnectedAccountId,
            Description = "Direct checkout - deferred payment (charged at deadline)",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await paymentRepository.AddAsync(payment);

        return setupIntent.ClientSecret ?? string.Empty;
    }

    /// <summary>
    /// "Pay at the property" (D5, BK-06): the booking stays Pending as a request. The payment row records the amount due
    /// at the property; the guest gets the "request received" email with the link that confirms the address and sends
    /// the request to the host.
    /// </summary>
    private async Task OpenOnSiteRequestAsync(Booking booking, string emailToken)
    {
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = Core.Entities.PaymentMethod.CashOnArrival,
            Description = "Direct checkout - payment on site",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await paymentRepository.AddAsync(payment);

        logger.LogInformation(
            "On-site request {BookingId} created: waiting for the guest's email confirmation until {RequestExpiresAt:o}",
            booking.Id,
            booking.RequestExpiresAt);
        await onSiteNotifier.RequestReceivedAsync(booking.Id, emailToken);
    }

    public async Task<Booking> UpdateBookingAsync(Booking booking)
    {
        var existingBooking = await repository.GetByIdAsync(booking.Id);
        if (existingBooking == null)
            throw new KeyNotFoundException($"Booking {booking.Id} not found");

        var validationResult = BookingValidator.ValidateBookingUpdate(existingBooking, booking);
        if (!validationResult.IsValid)
        {
            logger.LogWarning("Booking update validation failed: {Errors}", validationResult.ErrorMessage);
            throw new DomainRuleException("booking_update_invalid", "BookingUpdateInvalid");
        }

        var datesUnchanged = booking.CheckInDate == existingBooking.CheckInDate &&
            booking.CheckOutDate == existingBooking.CheckOutDate;
        var bookingValidation = BookingValidator.ValidateBooking(
            booking,
            allowPastCheckIn: datesUnchanged,
            today: _clock.TodayInRome());
        if (!bookingValidation.IsValid)
        {
            logger.LogWarning("Booking validation failed: {Errors}", bookingValidation.ErrorMessage);
            throw new DomainRuleException("booking_update_invalid", "BookingUpdateInvalid");
        }

        logger.LogInformation("Updating booking {Id}", booking.Id);
        try
        {
            return await repository.UpdateAsync(booking);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Property not available", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
        }
    }

    public async Task<bool> IsPropertyAvailableAsync(
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        int? pendingDirectTtlMinutes = null)
    {
        var effectivePendingTtlMinutes = pendingDirectTtlMinutes ?? GetPendingDirectTtlMinutes();

        // Expired checkout holds of these dates are released first, by the same routine as the expiry job (BK-21): the
        // intent is cancelled on Stripe, and a hold whose guest has paid meanwhile keeps its dates. Holds of other
        // dates are left to the job.
        await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(propertyId, checkIn, checkOut);

        if (!await repository.IsAvailableAsync(propertyId, checkIn, checkOut, effectivePendingTtlMinutes))
            return false;

        return !await propertyICalSyncService.HasOverlappingBlockAsync(propertyId, checkIn, checkOut);
    }

    public async Task<IEnumerable<Booking>> GetCalendarAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        // Read only: expired checkout holds are left out (they no longer take their dates) and cancelled by the job.
        return await repository.GetByDateRangeAsync(propertyId, startDate, endDate, GetPendingDirectTtlMinutes());
    }

    private int GetPendingDirectTtlMinutes() => CheckoutHolds.GetTtlMinutes(configuration);

    private async Task<Guest> CreateGuestSnapshotWithConsentAsync(
        Guid orgId,
        DirectBookingGuestInput guestInput,
        string consentVersion,
        string consentIpAddress)
    {
        var now = DateTime.UtcNow;

        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = guestInput.FirstName,
            LastName = guestInput.LastName,
            Email = guestInput.Email,
            PhoneNumber = guestInput.Phone ?? string.Empty,
            Country = guestInput.Country,
            DataProcessingConsentDate = now,
            ConsentIpAddress = consentIpAddress.Length > 50 ? consentIpAddress[..50] : consentIpAddress,
            ConsentVersion = consentVersion,
            ConsentDate = now,
            DataProcessingPurpose = "Direct Booking Checkout",
            CreatedAt = now,
            UpdatedAt = now,
        };

        return await guestRepository.AddAsync(guest);
    }
}
