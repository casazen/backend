using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
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
    ITaxCalculationService taxCalculationService,
    IStripeService stripeService,
    IPaymentRepository paymentRepository,
    PropertyICalSyncService propertyICalSyncService,
    IConfiguration configuration,
    ILogger<BookingService> logger,
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
        if (!Enum.IsDefined(input.PaymentOption))
        {
            throw new DirectBookingException(
                "Invalid payment option",
                DirectBookingErrorCodes.InvalidPaymentOption);
        }

        var allowedConsentVersion = configuration["DirectBooking:ConsentVersion"] ?? "2026-06-direct-checkout-v1";
        if (!string.Equals(input.ConsentVersion, allowedConsentVersion, StringComparison.Ordinal))
        {
            throw new DirectBookingException(
                "Invalid consent version",
                DirectBookingErrorCodes.InvalidConsentVersion);
        }

        var property = await propertyRepository.GetByIdAsync(input.PropertyId);
        if (property is null || !property.IsActive)
        {
            throw new DirectBookingException("Property not found", DirectBookingErrorCodes.PropertyNotFound);
        }

        if (property.ComplianceStatus != PropertyComplianceStatus.Active)
        {
            throw new DirectBookingException("Property not found", DirectBookingErrorCodes.PropertyNotFound);
        }

        var org = await orgService.GetByIdAsync(property.OrgId);
        if (org is null ||
            string.IsNullOrWhiteSpace(org.StripeConnectedAccountId) ||
            !org.ConnectChargesEnabled)
        {
            throw new DirectBookingException(
                "Complete Stripe onboarding before accepting guest payments",
                DirectBookingErrorCodes.PaymentNotReady);
        }

        var totalGuests = input.NumberOfAdults + input.NumberOfChildren;
        if (totalGuests > property.MaxGuests)
        {
            throw new DirectBookingException(
                $"This property allows a maximum of {property.MaxGuests} guests.",
                DirectBookingErrorCodes.TooManyGuests);
        }

        var checkIn = DateTime.SpecifyKind(input.CheckInDate.Date, DateTimeKind.Utc);
        var checkOut = DateTime.SpecifyKind(input.CheckOutDate.Date, DateTimeKind.Utc);
        if (checkOut <= checkIn)
        {
            throw new DirectBookingException(
                "Check-out date must be after check-in date",
                DirectBookingErrorCodes.InvalidDates);
        }

        var pendingTtlMinutes = GetPendingDirectTtlMinutes();

        if (!await IsPropertyAvailableAsync(input.PropertyId, checkIn, checkOut, pendingTtlMinutes))
        {
            throw new DirectBookingException(
                "Property not available for selected dates",
                DirectBookingErrorCodes.NotAvailable);
        }

        var guest = await CreateGuestSnapshotWithConsentAsync(
            property.OrgId, input.Guest, input.ConsentVersion, input.ConsentIpAddress);

        var nights = (checkOut - checkIn).Days;
        var basePrice = property.NightlyRate * nights + property.CleaningFee;
        var touristTaxAmount = await taxCalculationService.CalculateTouristTaxAsync(
            input.PropertyId, checkIn, checkOut, totalGuests);
        var totalPrice = basePrice + touristTaxAmount;
        var currency = "EUR";
        var freeRefundDeadline = checkIn.AddDays(-7);

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
            TouristTax = touristTaxAmount,
            TouristTaxAmount = touristTaxAmount,
            TotalPrice = totalPrice,
            PaymentOption = input.PaymentOption,
            FreeRefundDeadline = freeRefundDeadline,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var validationResult = BookingValidator.ValidateBooking(booking, today: _clock.TodayInRome());
        if (!validationResult.IsValid)
        {
            throw new DirectBookingException(
                validationResult.ErrorMessage ?? "Booking validation failed",
                DirectBookingErrorCodes.InvalidDates);
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
            throw new DirectBookingException(
                "Property not available for selected dates",
                DirectBookingErrorCodes.NotAvailable);
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
                await HandleOnSitePaymentAsync(createdBooking);
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
            input.PaymentOption);
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
            throw new DirectBookingException("Payment initialization failed", DirectBookingErrorCodes.StripeError);
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
            throw new DirectBookingException("Payment initialization failed", DirectBookingErrorCodes.StripeError);
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
            Description = "Direct checkout - deferred payment (charged at deadline)",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await paymentRepository.AddAsync(payment);

        return setupIntent.ClientSecret ?? string.Empty;
    }

    private async Task HandleOnSitePaymentAsync(Booking booking)
    {
        booking.Status = BookingStatus.Confirmed;
        booking.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(booking);

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

    public async Task<bool> CancelBookingAsync(Guid bookingId)
    {
        var booking = await repository.GetByIdAsync(bookingId);
        if (booking == null)
            return false;

        booking.Status = BookingStatus.Cancelled;
        booking.CheckoutReminderJobId = null;
        await repository.UpdateAsync(booking);
        logger.LogInformation("Booking {Id} cancelled", bookingId);
        return true;
    }

    public async Task<bool> IsPropertyAvailableAsync(
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        int? pendingDirectTtlMinutes = null)
    {
        var effectivePendingTtlMinutes = pendingDirectTtlMinutes ?? GetPendingDirectTtlMinutes();
        await repository.CancelExpiredPendingDirectBookingsAsync(
            propertyId, checkIn, checkOut, effectivePendingTtlMinutes);

        if (!await repository.IsAvailableAsync(propertyId, checkIn, checkOut, effectivePendingTtlMinutes))
            return false;

        return !await propertyICalSyncService.HasOverlappingBlockAsync(propertyId, checkIn, checkOut);
    }

    public async Task<IEnumerable<Booking>> GetCalendarAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        await repository.CancelExpiredPendingDirectBookingsAsync(
            propertyId, startDate, endDate, GetPendingDirectTtlMinutes());
        return await repository.GetByDateRangeAsync(propertyId, startDate, endDate);
    }

    private int GetPendingDirectTtlMinutes()
    {
        return Math.Max(1, configuration.GetValue("DirectBooking:PendingTtlMinutes", 15));
    }

    private async Task<Guest> CreateGuestSnapshotWithConsentAsync(
        Guid orgId,
        DirectBookingGuestInput guestInput,
        string consentVersion,
        string consentIpAddress)
    {
        var now = DateTime.UtcNow;
        var retentionUntil = now.AddYears(7);

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
            DataRetentionUntil = retentionUntil,
            DataProcessingPurpose = "Direct Booking Checkout",
            CreatedAt = now,
            UpdatedAt = now,
        };

        return await guestRepository.AddAsync(guest);
    }
}
