using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Infrastructure.Events;
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
    IPricingAdapterService pricingAdapterService,
    TimeProvider? timeProvider = null,
    IStayDiscountService? stayDiscountService = null,
    IDomainEventRecorder? domainEvents = null,
    ICrmContactService? crmContacts = null,
    IMarketingConfirmationService? marketingConfirmation = null,
    ICrmContactIndex? contactIndex = null,
    IChannelManagerAvailability? channelManager = null) : IBookingService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<Booking?> GetBookingAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public Task<IReadOnlyList<Booking>> GetBookingsAsync(
        HostScope scope,
        Guid? propertyId = null,
        Guid? guestId = null,
        CancellationToken cancellationToken = default) =>
        repository.GetByScopeAsync(scope, propertyId, guestId, cancellationToken);

    public async Task<Booking> CreateManualBookingAsync(Booking booking, Guest guest)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(guest);
        if (booking.OrgId == Guid.Empty)
            throw new ArgumentException("A manual booking must carry the org of its property.", nameof(booking));

        // PM-01: a property in long-term mode takes no stay (422). The host endpoint refuses it earlier, with the property
        // it has already read; this is the check of the service that writes the booking. A property that is not found is
        // left to the checks below and to the foreign key, as before.
        var property = await propertyRepository.GetRecordAsync(booking.PropertyId);
        if (property is not null)
            PropertyRentalModeRules.EnsureShortRent(property);

        // A booking entered by the host is a confirmed stay from the start: it occupies its dates on the booking site
        // and in the iCal export, and the checkout hold expiry never cancels it (PC-01, A2-01).
        booking.Status = BookingStatus.Confirmed;
        booking.ConfirmedAt = _clock.GetUtcNow().UtcDateTime;
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

        // E08/F01/US02/T01 (NF-80): the channel manager is asked too, before anything is written (it may save the room of the mapping).
        await EnsureChannelManagerAcceptsAsync(booking.OrgId, booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, booking.NumberOfGuests);

        // The guest snapshot is written only once the dates are known to be free, and removed again if a concurrent
        // booking takes them before the insert (the repository re-checks under the property lock).
        await guestRepository.AddAsync(guest);
        try
        {
            logger.LogInformation("Creating manual booking for property {PropertyId}", booking.PropertyId);
            // The host entered it, confirmed from the start: created and confirmed, saved with the booking by the repository (the
            // events of a booking the repository refuses go with it, ADR-022-R1).
            BookingEvents.RecordCreated(domainEvents, booking, BookingEventOrigin.Host);
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

        // DB-03 / NF-90: the optional "send me offers" boxes, one for each channel (e-mail, WhatsApp). As on the check-in portal (CO-15) a
        // consent exists only with a versioned text (Gdpr:MarketingConsentVersion): the version is recorded with the consent as the proof of
        // what the guest agreed to. Without one a consent cannot be recorded properly, so it is refused instead of silently dropped. The
        // legacy single box counts as the e-mail channel.
        var marketingVersion = GdprOptions.Normalize(
            configuration[$"{GdprOptions.SectionName}:{nameof(GdprOptions.MarketingConsentVersion)}"]);
        var emailConsent = input.MarketingConsent || input.MarketingConsentEmail;
        var whatsAppConsent = input.MarketingConsentWhatsApp;
        if ((emailConsent || whatsAppConsent) && marketingVersion is null)
        {
            throw new DomainRuleException(
                DirectBookingErrorCodes.MarketingConsentUnavailable, "DirectBookingMarketingConsentUnavailable");
        }

        // A consent on a channel needs the address of that channel: WhatsApp needs a phone number the CRM can store and reach.
        if (whatsAppConsent && CrmNormalizer.NormalizePhone(input.Guest.Phone, DefaultCallingCode()) is null)
            throw new DomainRuleException(DirectBookingErrorCodes.WhatsAppRequiresPhone, "DirectBookingWhatsAppRequiresPhone");

        // NF-90: the soft opt-in notice (art. 130 c. 4 Codice privacy). The version the page showed must be the one in force, else the notice
        // the guest read is not the notice we would record as presented.
        var softOptInNoticeVersion = ResolveSoftOptInNotice(input.SoftOptInNoticeVersion);

        // DB-03b: the optional expected arrival time is a note for the host: only its shape is checked, before anything is written.
        if (!ArrivalTimeRules.TryNormalize(input.ExpectedArrivalTime, out var expectedArrivalTime))
            throw new DomainRuleException(DirectBookingErrorCodes.ArrivalTimeInvalid, "DirectBookingArrivalTimeInvalid");

        var property = await GetBookablePropertyAsync(input.PropertyId);

        var org = await orgService.GetByIdAsync(property.OrgId);
        if (org is null || !org.CanTakeDirectPayments)
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

        // BK-18 (A3-33): the guest snapshot (personal data and consent) is only built in memory here. It is written with
        // the booking, in the same insert under the property lock, once the stay is validated and the dates are free: a
        // rejected checkout leaves no guest behind.
        var guest = NewGuestSnapshotWithConsent(
            property.OrgId, input.Guest, input.ConsentVersion, input.ConsentIpAddress);

        // DB-03 / NF-90: the optional marketing consents (one row for each channel the guest ticked) and the soft opt-in notice that was
        // presented are recorded in the append-only register of the privacy history (version of the text, time, source, IP), in the same
        // insert as the booking and the guest; the e-mail consent is also recorded on the guest.
        var consentAt = _clock.GetUtcNow().UtcDateTime;
        var consentRecords = RecordCheckoutConsents(
            guest, emailConsent, whatsAppConsent, marketingVersion, softOptInNoticeVersion, input.ConsentIpAddress, consentAt);

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
            Guest = guest,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfAdults = input.NumberOfAdults,
            NumberOfChildren = input.NumberOfChildren,
            NumberOfGuests = totalGuests,
            SpecialRequests = input.SpecialRequests ?? string.Empty,
            ExpectedArrivalTime = expectedArrivalTime,
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

        if (!await IsPropertyAvailableAsync(input.PropertyId, checkIn, checkOut, GetPendingDirectTtlMinutes()))
        {
            throw new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
        }

        // E08/F01/US02/T01 (NF-80): the channel manager is asked too, before the hold is written and its event recorded (the lookup may save
        // the room of the mapping, which must not carry the pending rows of the checkout with it).
        await EnsureChannelManagerAcceptsAsync(property.OrgId, input.PropertyId, checkIn, checkOut, totalGuests);

        Booking createdBooking;
        try
        {
            // The hold is created (Pending): its event is saved with it, in the same insert (a checkout whose payment cannot be started
            // is removed with its events, BK-18). The confirmation follows with the payment.
            BookingEvents.RecordCreated(domainEvents, booking, BookingEventOrigin.Guest);

            // Booking and guest snapshot (and the privacy history of the checkout, when there is some) in one insert: a concurrent
            // checkout that takes the dates first leaves none of them.
            createdBooking = consentRecords.Count == 0
                ? await repository.AddAsync(booking)
                : await repository.AddWithGuestConsentsAsync(booking, consentRecords);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Property not available", StringComparison.OrdinalIgnoreCase))
        {
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

        try
        {
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
        }
        catch (Exception)
        {
            // BK-18 (A3-33): the guest never got a payment to complete, so the attempt is not a booking. It is removed
            // with its guest snapshot instead of staying as a cancelled booking that keeps the guest's personal data.
            await DiscardFailedCheckoutAsync(createdBooking.Id);
            throw;
        }

        // NF-90: the guest has a payment to complete (or a request to confirm), so the attempt is a booking: the consents they ticked become
        // contacts of the CRM and the double opt-in e-mail goes out. Best effort: the privacy history is already saved with the booking.
        await RecordCrmConsentsAsync(
            guest,
            property.OrgId,
            org.DefaultLanguage,
            emailConsent,
            whatsAppConsent,
            marketingVersion,
            input.ConsentIpAddress,
            consentAt,
            createdBooking.Id);

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

    /// <summary>
    /// A short-rent, active, unpaused property whose compliance allows public bookings and that its host shows on the site:
    /// <see cref="PublicListing.IsPublished"/> itself. 404 when it does not exist or is not published (PC-03, A2-05); a property in
    /// long-term mode (PM-01) is answered with the reason, 422 <c>property_not_bookable_in_long_mode</c>, whatever its
    /// other state: the public pages never lead to it, so this is a stale page or a hand-made request.
    /// </summary>
    private async Task<Property> GetBookablePropertyAsync(Guid propertyId)
    {
        var property = await propertyRepository.GetByIdAsync(propertyId);
        if (property is null)
            throw new NotFoundException("Property not bookable") { MessageKey = "PropertyNotFound" };

        PropertyRentalModeRules.EnsureShortRent(property);

        // The rule is read from PublicListing, never copied: a property the host switched off the site (SR-07a) is as unbookable as
        // a paused one.
        if (!PublicListing.Includes(property))
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

        // PM-01: the stays of a property in long-term mode are not priced nor entered (host quote, new and changed manual
        // booking). Before the guests check: a long-term property has no guest capacity, "0 guests" would hide the reason.
        PropertyRentalModeRules.EnsureShortRent(property);

        if (numberOfAdults + numberOfChildren > property.MaxGuests)
            throw new DomainRuleException(BookingErrorCodes.TooManyGuests, "BookingTooManyGuests", property.MaxGuests);

        var checkIn = checkInDate.Date;
        var checkOut = checkOutDate.Date;
        if (checkOut <= checkIn || (checkOut - checkIn).Days > TouristTaxCalculator.MaxNights)
            throw new DomainRuleException(BookingErrorCodes.InvalidDates, "BookingInvalidDates");

        return await PriceStayAsync(
            property, checkIn, checkOut, numberOfAdults, numberOfChildren, childrenAges, cancellationToken);
    }

    /// <summary>
    /// Guests within the capacity and a stay of 1 to <see cref="TouristTaxCalculator.MaxNights"/> nights, not shorter than
    /// the minimum stay of the property (DB-03, 422 <see cref="DirectBookingErrorCodes.MinNightsNotMet"/>). The minimum is a
    /// rule of the guests' quote and checkout, the two callers of this method: the host's own prices and bookings
    /// (<see cref="PriceHostStayAsync"/>, manual bookings) do not go through it, a stay entered by hand can be shorter.
    /// </summary>
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

        if (property.MinNights is { } minNights && (checkOut - checkIn).Days < minNights)
        {
            throw new DomainRuleException(DirectBookingErrorCodes.MinNightsNotMet, "DirectBookingMinNightsNotMet", minNights);
        }

        return (checkIn, checkOut);
    }

    /// <summary>
    /// The nights (<see cref="StayPricing.Lodging"/>: nightly rate, with the weekend surcharge of the property on its Friday
    /// and Saturday nights) + the cleaning fee, plus the tourist tax of <see cref="ITouristTaxQuoteService"/> when it can
    /// be calculated. The property has no accommodation category (yet): only the rates for every accommodation of the
    /// comune apply. The night price for percentage rates is the price of that night, cleaning excluded (the nightly rate,
    /// or the rate with the surcharge on a weekend night). The payment options come from the free refund deadline of the
    /// stay (<see cref="DirectBookingPaymentRules"/>, A3-16). The length-of-stay discount of the property (NF-35) is taken off the
    /// lodging, and the base price is what remains plus the cleaning fee. With no surcharge and no discount, the default, the price
    /// is what it always was.
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
        // The stay is priced, and later charged and refunded, in the currency of its property (CHF-MONEY): the quote carries it, and the
        // PaymentIntent of the checkout takes it from the quote. A currency the product does not take is refused before any price is given.
        var currency = StayCurrency.ForProperty(property);
        if (!StayCurrency.IsSupported(currency))
            throw new DomainRuleException(DirectBookingErrorCodes.CurrencyNotSupported, "DirectBookingCurrencyNotSupported");

        var nights = (checkOut - checkIn).Days;
        var checkInDate = RomeCalendar.DateInRome(checkIn);

        // PC-15: a price the host confirmed for a date is the price of that night (neither the rate nor the weekend surcharge
        // of DB-03 applies to it); the nights without one cost the rate, plus the surcharge on a Friday or a Saturday.
        var hostPrices = await pricingAdapterService.GetAppliedNightlyPricesAsync(
            property.Id, checkInDate, checkInDate.AddDays(nights), cancellationToken);

        // NF-35: the length-of-stay discounts of the property, read once for this quote. Every path that prices a stay (the guest's quote and
        // checkout, the host's quote and manual booking) comes through here, so they all give the same price, to the cent: the discount is
        // taken off the lodging only, never off the cleaning fee or the tourist tax.
        IReadOnlyList<StayDiscountTier> discountTiers = [];
        if (stayDiscountService is not null)
            discountTiers = await stayDiscountService.GetForPricingAsync(property.Id, cancellationToken);

        var lodging = StayPricing.Lodging(checkInDate, nights, property.NightlyRate, property.WeekendSurchargePercent, hostPrices, discountTiers);
        var basePrice = lodging.NetTotal + property.CleaningFee;
        var touristTax = await touristTaxQuoteService.QuoteAsync(
            TouristTaxComune.ForProperty(property),
            new TouristTaxStay(
                checkInDate,
                RomeCalendar.DateInRome(checkOut),
                adults,
                children,
                childrenAges,
                AccommodationCategory: null,
                NightlyPrice: property.NightlyRate,
                NightlyPrices: StayPricing.NightPrices(checkInDate, lodging)),
            cancellationToken);

        // DB-08a: the policy of THIS stay (the catalog policy with the host's overrides for a stay under 28 nights), the one the
        // cancellation will apply, so the day of the deferred charge and the free cancellation shown are true. It is stored on the
        // booking as it is at creation: the bookings made before keep the day they were given.
        var effectivePolicy = CancellationRefundPolicy.EffectivePolicy(property, nights);
        var freeRefundDeadline = DirectBookingPaymentRules.FreeRefundDeadline(checkIn, effectivePolicy);

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
            currency,
            freeRefundDeadline,
            DirectBookingPaymentRules.OptionsFor(freeRefundDeadline, _clock.TodayInRome(), effectivePolicy),
            lodging,
            // DB-03b: the structured dates of the same policy (the last whole days, Europe/Rome), for the page to draw «fino al…».
            PublicCancellation.StayOf(effectivePolicy, checkIn, _clock.TodayInRomeAsDateOnly()));
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
                StayCurrency.ForStripe(currency),
                metadata);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe PaymentIntent creation failed for booking {BookingId}", booking.Id);
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

    /// <summary>
    /// The second half of the final check on the dates (E08/F01/US02/T01, NF-74, NF-80): the channel manager is asked besides CasaZen's own
    /// occupancy, and an Octorate that answers "not available" refuses the booking with the same 409 as a night CasaZen has taken
    /// (<see cref="BookingErrorCodes.DatesUnavailable"/>, the same text: the guest is not told about the channel manager). A property that is
    /// not mirrored, an Octorate that cannot be asked (timeout, 429, 5xx, a room not chosen) or the connector being off never refuses: the create
    /// push of the booking (E08/F01/US01/T03) reports a conflict if Octorate refuses later. Called before anything of the booking is written or
    /// recorded, because the lookup of the target may save the room of the mapping.
    /// </summary>
    private async Task EnsureChannelManagerAcceptsAsync(Guid orgId, Guid propertyId, DateTime checkIn, DateTime checkOut, int guests)
    {
        if (channelManager is null)
            return;

        var answer = await channelManager.CheckAsync(
            propertyId,
            orgId,
            DateOnly.FromDateTime(checkIn),
            DateOnly.FromDateTime(checkOut),
            Math.Max(1, guests),
            CancellationToken.None);
        if (!answer.IsNotAvailable)
            return;

        logger.LogInformation(
            "Booking rejected: property {PropertyId} is not available on the channel manager from {CheckIn:yyyy-MM-dd} to {CheckOut:yyyy-MM-dd}",
            propertyId,
            checkIn,
            checkOut);
        throw new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
    }

    /// <summary>
    /// Removes a checkout whose payment could not be started (BK-18). Best effort: when the removal fails too, the attempt
    /// stays a pending hold that the checkout hold expiry cancels, and the original error is still the answer.
    /// </summary>
    private async Task DiscardFailedCheckoutAsync(Guid bookingId)
    {
        try
        {
            await repository.DiscardCheckoutAttemptAsync(bookingId);
            logger.LogWarning("Direct checkout {BookingId} discarded with its guest snapshot: payment not started", bookingId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Direct checkout {BookingId} could not be discarded after its payment failed to start; it stays a pending hold",
                bookingId);
        }
    }

    /// <summary>
    /// The rows of the privacy history of the checkout (DB-03, E07/F04/US01/T02, NF-90), not saved: they are written with the booking and the
    /// guest, in one transaction. One <see cref="GuestConsentPurpose.Marketing"/> row for each channel the guest ticked, with the version of
    /// the text the guest saw, the source, the time and the IP as the check-in portal records its own, and, when the page showed the soft
    /// opt-in notice in force, one <see cref="GuestConsentPurpose.SoftOptInNotice"/> row (the notice is presented, never a consent). The
    /// e-mail consent also marks the snapshot (<c>Guest.MarketingConsent</c> and its date): the yes/no the withdrawal that a host records on a
    /// guest's request looks at. A WhatsApp consent alone does not, because that flag is read as a consent to the e-mail address. Each consent row
    /// names its channel (<see cref="GuestConsentChannelNote"/>): without it the rows of a guest who ticks both channels would be the same.
    /// </summary>
    private static List<GuestConsentRecord> RecordCheckoutConsents(
        Guest guest,
        bool email,
        bool whatsApp,
        string? marketingVersion,
        string? noticeVersion,
        string consentIpAddress,
        DateTime at)
    {
        var records = new List<GuestConsentRecord>(3);
        if (marketingVersion is not null)
        {
            if (email)
            {
                guest.MarketingConsent = true;
                guest.MarketingConsentDate = at;
                records.Add(NewCheckoutRecord(
                    guest, GuestConsentPurpose.Marketing, GuestConsentAction.Granted, marketingVersion, consentIpAddress, at, GuestConsentChannelNote.Email));
            }

            if (whatsApp)
            {
                records.Add(NewCheckoutRecord(
                    guest, GuestConsentPurpose.Marketing, GuestConsentAction.Granted, marketingVersion, consentIpAddress, at, GuestConsentChannelNote.WhatsApp));
            }
        }

        if (noticeVersion is not null)
        {
            records.Add(NewCheckoutRecord(
                guest, GuestConsentPurpose.SoftOptInNotice, GuestConsentAction.NoticePresented, noticeVersion, consentIpAddress, at, null));
        }

        return records;
    }

    private static GuestConsentRecord NewCheckoutRecord(
        Guest guest,
        GuestConsentPurpose purpose,
        GuestConsentAction action,
        string version,
        string consentIpAddress,
        DateTime at,
        string? channelNote)
    {
        return new GuestConsentRecord
        {
            OrgId = guest.OrgId,
            GuestId = guest.Id,
            Purpose = purpose,
            Action = action,
            Version = version,
            Source = GuestConsentSource.BookingCheckout,
            IpAddress = string.IsNullOrWhiteSpace(consentIpAddress)
                ? null
                : (consentIpAddress.Length > 50 ? consentIpAddress[..50] : consentIpAddress),
            Note = channelNote,
            RecordedAt = at,
        };
    }

    /// <summary>
    /// The version of the soft opt-in notice to record as presented, or <c>null</c> when the checkout sent none. A version that is not the
    /// configured one (<c>Gdpr:SoftOptInNoticeVersion</c>; none configured counts) is a notice the guest read that is no longer in force:
    /// 409, so it is never recorded as presented.
    /// </summary>
    private string? ResolveSoftOptInNotice(string? versionSent)
    {
        var sent = GdprOptions.Normalize(versionSent);
        if (sent is null)
            return null;

        var current = GdprOptions.Normalize(
            configuration[$"{GdprOptions.SectionName}:{nameof(GdprOptions.SoftOptInNoticeVersion)}"]);
        if (current is null || !string.Equals(sent, current, StringComparison.Ordinal))
            throw new DomainConflictException(DirectBookingErrorCodes.SoftOptInNoticeOutdated, "DirectBookingSoftOptInNoticeOutdated");

        return current;
    }

    private string? DefaultCallingCode() =>
        configuration[$"{CrmOptions.SectionName}:{nameof(CrmOptions.DefaultCallingCode)}"];

    /// <summary>
    /// Makes the consents the guest ticked contacts of the CRM and sends the double opt-in e-mail of the address (E07/F04/US01/T02, NF-90,
    /// CRM-03): origin <see cref="GuestContactOrigin.DirectBooking"/>, source <see cref="GuestConsentSource.BookingCheckout"/>, with the
    /// version, the IP, the time and the default language of the org. The privacy history is already saved with the booking, so the row is
    /// not written twice (<see cref="CrmConsentInput.RecordHistory"/>). <b>Best effort and never fatal</b>: the checkout does not depend
    /// on the CRM. Without the key of the contact index (the CRM is closed in this environment) nothing is made and the sync of the CRM makes
    /// the contacts once it is open; a contact or an e-mail that fails is logged by id and error type, never by address, and the host can
    /// send the confirmation again.
    /// </summary>
    private async Task RecordCrmConsentsAsync(
        Guest guest,
        Guid orgId,
        string? orgLanguage,
        bool email,
        bool whatsApp,
        string? marketingVersion,
        string consentIpAddress,
        DateTime at,
        Guid bookingId)
    {
        if ((!email && !whatsApp) || marketingVersion is null || crmContacts is null || contactIndex is not { IsConfigured: true })
            return;

        try
        {
            var ip = string.IsNullOrWhiteSpace(consentIpAddress) ? null : consentIpAddress;
            var displayName = $"{guest.FirstName} {guest.LastName}".Trim();
            if (email)
            {
                var contact = await crmContacts.RecordConsentAsync(new CrmConsentInput(
                    orgId,
                    GuestContactChannel.Email,
                    guest.Email,
                    GuestContactOrigin.DirectBooking,
                    marketingVersion,
                    GuestConsentSource.BookingCheckout,
                    ip,
                    at,
                    orgLanguage,
                    displayName,
                    GuestId: guest.Id,
                    BookedVia: BookingSource.Direct,
                    RecordHistory: false));

                if (contact is not null && marketingConfirmation is not null)
                    await marketingConfirmation.SendConfirmationAsync(orgId, contact.Id);
            }

            if (whatsApp)
            {
                await crmContacts.RecordConsentAsync(new CrmConsentInput(
                    orgId,
                    GuestContactChannel.WhatsApp,
                    guest.PhoneNumber,
                    GuestContactOrigin.DirectBooking,
                    marketingVersion,
                    GuestConsentSource.BookingCheckout,
                    ip,
                    at,
                    orgLanguage,
                    displayName,
                    GuestId: guest.Id,
                    BookedVia: BookingSource.Direct,
                    RecordHistory: false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "The CRM contact of the marketing consent of checkout {BookingId} could not be made ({Error}); the privacy history has the consent and the CRM sync makes the contact",
                bookingId,
                CrmDatabaseErrors.Describe(ex));
        }
    }

    /// <summary>The guest of a direct checkout with its consent, not saved: it is written with its booking.</summary>
    private static Guest NewGuestSnapshotWithConsent(
        Guid orgId,
        DirectBookingGuestInput guestInput,
        string consentVersion,
        string consentIpAddress)
    {
        var now = DateTime.UtcNow;

        return new Guest
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
    }
}
