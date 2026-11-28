namespace Casazen.Core.Services;

using Casazen.Core.Bookings;
using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

public sealed record DirectCheckoutExtra(Guid Id, int Quantity, decimal ExpectedUnitPrice);
public sealed record DirectCheckoutExtraOffer(Guid Id, string Name, string Description, decimal UnitPrice,
    string Currency, bool ByQuantity, bool RequiresApproval);

public record DirectBookingGuestInput(
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    string Country);

/// <param name="MarketingConsent">
/// The guest ticked the optional box that agrees to receive offers (DB-03): never preselected, never required. Honoured only
/// when the marketing consent text has a version (<c>Gdpr:MarketingConsentVersion</c>, CO-15): the version is recorded with
/// the consent, so without one the answer is 422 <see cref="DirectBookingErrorCodes.MarketingConsentUnavailable"/>.
/// </param>
/// <param name="ExpectedArrivalTime">
/// When the guest thinks they will arrive (DB-03b): <c>HH:mm</c> or <c>late</c> (after the check-in window), <c>null</c> when they
/// do not say. Only a note for the host (<see cref="ArrivalTimeRules"/>): it changes no price, no availability and no check.
/// </param>
/// <param name="MarketingConsentEmail">
/// The guest ticked the optional box for offers by e-mail (E07/F04/US01/T02, NF-90): never preselected, never required. The legacy
/// <paramref name="MarketingConsent"/> counts as this channel. Like every consent it needs the version of its text.
/// </param>
/// <param name="MarketingConsentWhatsApp">
/// The guest ticked the optional box for offers on WhatsApp: never preselected, never required. Needs a phone number that can be reached, else
/// 422 <see cref="DirectBookingErrorCodes.WhatsAppRequiresPhone"/>.
/// </param>
/// <param name="SoftOptInNoticeVersion">
/// The version of the soft opt-in notice (art. 130 c. 4 Codice privacy) the checkout showed, <c>null</c> when it showed none. The booking
/// records that the notice was presented only when it is the configured one (<c>Gdpr:SoftOptInNoticeVersion</c>); any other is 409
/// <see cref="DirectBookingErrorCodes.SoftOptInNoticeOutdated"/>.
/// </param>
/// <param name="Attribution">
/// Where the guest came from (E27/F01/US01/T01, NF-123): the <c>utm_*</c> of the link and the page the guest came from, as the checkout sent them, <c>null</c> when
/// it sent none. The service keeps only what passes <see cref="BookingAttribution.Sanitize"/>; a value that does not is dropped, and the booking never fails for it.
/// </param>
public record DirectBookingCreateInput(
    Guid PropertyId,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int NumberOfAdults,
    int NumberOfChildren,
    DirectBookingGuestInput Guest,
    string ConsentVersion,
    string ConsentIpAddress,
    string? SpecialRequests,
    PaymentOption PaymentOption = PaymentOption.Immediate,
    IReadOnlyList<int>? ChildrenAges = null,
    bool MarketingConsent = false,
    string? ExpectedArrivalTime = null,
    bool MarketingConsentEmail = false,
    bool MarketingConsentWhatsApp = false,
    string? SoftOptInNoticeVersion = null,
    BookingAttributionInput? Attribution = null,
    IReadOnlyList<DirectCheckoutExtra>? Extras = null);

public record DirectBookingQuoteInput(
    Guid PropertyId,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int NumberOfAdults,
    int NumberOfChildren,
    IReadOnlyList<int>? ChildrenAges = null);

/// <summary>
/// Price of a direct booking: <c>TotalPrice = BasePrice + tourist tax</c> when the tax is calculated, otherwise
/// <c>BasePrice</c> (the tax is then not included and never invented). The tax is collected with the rest of the
/// total (spec-direct-checkout AC6).
/// </summary>
/// <param name="FreeRefundDeadline">
/// Last day of the full refund (<see cref="DirectBookingPaymentRules.FreeRefundDeadline"/>), recorded on the booking; also
/// the day the deferred payment is charged.
/// </param>
/// <param name="Currency">ISO 4217 code of the property (<see cref="StayCurrency"/>): every amount of the quote is in it, and the checkout charges in it.</param>
/// <param name="PaymentOptions">Which payment options the checkout may offer for this stay (A3-16).</param>
/// <param name="Lodging">
/// How the nights of the stay are priced (DB-03): the nights at the nightly rate and the weekend nights with the property's
/// surcharge, less the length-of-stay discount when a tier of the property applies (NF-35). <c>BasePrice = Lodging.NetTotal + CleaningFee</c>;
/// with no surcharge and no discount (the default) <c>Lodging.Total</c> is <c>NightlyRate x Nights</c> and equals <c>Lodging.NetTotal</c>, as
/// it has always been.
/// </param>
/// <param name="Cancellation">
/// What cancelling costs for this stay (DB-03b): the last days of the full and of the partial refund, on the policy of the stay, Europe/Rome
/// dates still ahead. Worked out from the same policy as <paramref name="FreeRefundDeadline"/> and the guest area.
/// </param>
public record DirectBookingQuote(
    Guid PropertyId,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int Nights,
    decimal NightlyRate,
    decimal CleaningFee,
    decimal BasePrice,
    TouristTaxQuote TouristTax,
    decimal TotalPrice,
    string Currency,
    DateTime FreeRefundDeadline,
    DirectBookingPaymentOptions PaymentOptions,
    StayLodging Lodging,
    StayCancellation? Cancellation = null)
{
    public IReadOnlyList<DirectCheckoutExtraOffer> ExtraOffers { get; init; } = [];
}

/// <summary>
/// The lodging part of a quote (DB-03, D20): <see cref="WeekdayNights"/> nights at <see cref="NightlyRate"/> and
/// <see cref="WeekendNights"/> nights at <see cref="WeekendNightlyRate"/>, the rate with the property's weekend surcharge
/// (<see cref="StayPricing.Lodging"/>), and the <see cref="PricedNights"/> that the host priced one by one. Without a surcharge
/// and without priced nights there are no weekend nights: every night, Friday and Saturday included, is a weekday night at
/// the nightly rate, so the lodging is what it has always been.
/// </summary>
/// <param name="WeekdayNights">Nights charged at <paramref name="NightlyRate"/> (all of them when the property has no surcharge).</param>
/// <param name="WeekendNights">Friday and Saturday nights charged at <paramref name="WeekendNightlyRate"/>; 0 without a surcharge.</param>
/// <param name="NightlyRate">The property's nightly rate.</param>
/// <param name="WeekendNightlyRate">The nightly rate plus the weekend surcharge, rounded to the cent (the rate itself when there is none).</param>
/// <param name="Total">
/// The price of all the nights, an exact number of cents, <b>before</b> the length-of-stay discount (<see cref="Discount"/>): what the guest pays
/// for the nights is <see cref="NetTotal"/>.
/// </param>
public sealed record StayLodging(
    int WeekdayNights,
    int WeekendNights,
    decimal NightlyRate,
    decimal WeekendNightlyRate,
    decimal Total)
{
    public StayDiscount? Discount { get; init; }

    public decimal NetTotal => Total - (Discount?.Amount ?? 0m);

    public IReadOnlyList<PricedNight> PricedNights { get; init; } = [];

    public int Nights => WeekdayNights + WeekendNights + PricedNights.Count;
}

public sealed record PricedNight(DateOnly Date, decimal Price);

public sealed record StayDiscountTier(int MinNights, decimal PercentOff);

public sealed record StayDiscount(int MinNights, decimal PercentOff, decimal Amount);

public enum QuoteLineKind
{
    Nights = 0,

    WeekendNights = 1,

    /// <summary>
    /// The length-of-stay discount (NF-35): a negative amount, taken off the nights only, never off the cleaning fee or the tourist tax; present
    /// only when a tier of the property applies. It comes after the night lines and before the cleaning fee; its number is the next free one, the
    /// other members keep theirs.
    /// </summary>
    LengthOfStayDiscount = 5,

    CleaningFee = 2,

    TouristTax = 3,

    Total = 4,
}

/// <summary>
/// One line of the price breakdown of a quote (DB-03), in cents so the frontend never adds euros with decimals.
/// </summary>
/// <param name="Kind">What the line is.</param>
/// <param name="Quantity">Number of nights for the night lines, 1 for the cleaning fee, <c>null</c> for the discount, the tax and the total.</param>
/// <param name="UnitAmountCents">Price of one night for the night lines, the fee for the cleaning fee; <c>null</c> otherwise.</param>
/// <param name="AmountCents">Amount of the line (quantity x unit for the night lines, negative for the length-of-stay discount); the total for <see cref="QuoteLineKind.Total"/>.</param>
public sealed record QuoteLine(QuoteLineKind Kind, int? Quantity, long? UnitAmountCents, long AmountCents);

/// <summary>Payment options of a stay, decided by the backend so the checkout never offers or promises what does not apply.</summary>
/// <param name="DeferredPaymentAvailable">
/// "Paga alla scadenza" (<see cref="PaymentOption.OnCancellationDeadline"/>) can be chosen: its charge day is after today in
/// Europe/Rome (<see cref="DirectBookingPaymentRules.IsDeferredPaymentOffered"/>).
/// </param>
/// <param name="DeferredChargeDate">Day the saved card is charged when the deferred payment is chosen (midnight UTC of the Rome date).</param>
/// <param name="FreeCancellationUntil">
/// Last day the guest can cancel for free by themselves from the guest area (DB-08a), a Europe/Rome date: the last day of the full
/// refund of the stay's policy. <c>null</c> when it would not be true: the property has no cancellation policy, the policy never
/// refunds in full, or the day is already past (<see cref="DirectBookingPaymentRules.IsFreeCancellationPromised"/>): the checkout then
/// promises no free cancellation.
/// </param>
public record DirectBookingPaymentOptions(
    bool DeferredPaymentAvailable,
    DateTime? DeferredChargeDate,
    DateTime? FreeCancellationUntil);

public record DirectBookingCreateResult(
    Guid BookingId,
    string ClientSecret,
    string PublishableKey,
    string StripeAccountId,
    decimal Amount,
    string Currency,
    decimal TouristTaxAmount,
    decimal BasePrice,
    string? SetupIntentClientSecret = null,
    DateTime? FreeRefundDeadline = null,
    PaymentOption PaymentOption = PaymentOption.Immediate,
    TouristTaxQuoteStatus TouristTaxStatus = TouristTaxQuoteStatus.Calculated,
    DateTime? OnSiteRequestExpiresAt = null,
    string CheckoutToken = "");

/// <summary>
/// Stable <c>code</c> values of the public checkout errors (<c>POST /api/public/bookings</c> and its quote, BK-06 / R-11,
/// BK-07): the frontend translates them (<c>apiErrors.codes.*</c>) instead of showing a generic "checkout failed". Dates
/// and guests reuse the booking codes (<see cref="BookingErrorCodes"/>); "pay at the property" requests have theirs in
/// <see cref="OnSiteRequestErrorCodes"/>; the outcome page in <see cref="CheckoutOutcomeErrorCodes"/>.
/// </summary>
public static class DirectBookingErrorCodes
{
    public const string PaymentsNotReady = "direct_booking_payments_not_ready";

    public const string InvalidStay = "direct_booking_invalid_stay";

    public const string ConsentRequired = "direct_booking_consent_required";

    /// <summary>422: the consent text changed since the page was loaded.</summary>
    public const string ConsentOutdated = "direct_booking_consent_outdated";

    public const string InvalidPaymentOption = "direct_booking_invalid_payment_option";

    public const string DeferredPaymentUnavailable = "direct_booking_deferred_payment_unavailable";

    public const string ChildAgesRequired = "tourist_tax_child_ages_required";

    public const string MinNightsNotMet = "direct_booking_min_nights_not_met";

    public const string ArrivalClosed = "direct_booking_arrival_closed";

    public const string DepartureClosed = "direct_booking_departure_closed";

    public const string MaxNightsExceeded = "direct_booking_max_nights_exceeded";

    public const string AdvanceTooShort = "direct_booking_advance_too_short";

    public const string AdvanceTooLong = "direct_booking_advance_too_long";

    /// <summary>
    /// 422: the checkout carries <c>marketingConsent: true</c> but the marketing consent text has no version
    /// (<c>Gdpr:MarketingConsentVersion</c>, CO-15): the consent would be recorded without proof of what the guest agreed to,
    /// so it is not offered (<c>marketingConsentVersion</c> of the public org is null) and not accepted either.
    /// </summary>
    public const string MarketingConsentUnavailable = "direct_booking_marketing_consent_unavailable";

    public const string WhatsAppRequiresPhone = "direct_booking_whatsapp_requires_phone";

    /// <summary>
    /// 409: the checkout carries a <c>softOptInNoticeVersion</c> that is not the current one (<c>Gdpr:SoftOptInNoticeVersion</c>, or none is
    /// configured any more): the notice the guest read is not the notice in force, so it is never recorded as presented.
    /// </summary>
    public const string SoftOptInNoticeOutdated = "direct_booking_soft_opt_in_notice_outdated";

    public const string ArrivalTimeInvalid = "direct_booking_arrival_time_invalid";

    public const string CurrencyNotSupported = "currency_not_supported";
}
