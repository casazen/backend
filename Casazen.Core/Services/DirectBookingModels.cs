namespace Casazen.Core.Services;

using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

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
    string? SoftOptInNoticeVersion = null);

/// <summary>Stay to price before booking (checkout quote).</summary>
/// <param name="ChildrenAges">Age of each minor at check-in, needed when the tourist tax depends on it.</param>
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
    StayCancellation? Cancellation = null);

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
    /// <summary>
    /// The length-of-stay discount taken off the nights (NF-35, ADR-028): the tier of the property that the stay reaches and the amount it takes
    /// off <see cref="Total"/>. <c>null</c> = none applies, which is every stay of a property with no tier.
    /// </summary>
    public StayDiscount? Discount { get; init; }

    /// <summary>What the guest pays for the nights: <see cref="Total"/> less the discount; the cleaning fee and the tourist tax are not in it.</summary>
    public decimal NetTotal => Total - (Discount?.Amount ?? 0m);

    /// <summary>
    /// The nights of the stay whose price the host set for their date (a seasonal price the host confirmed, PC-15), in date
    /// order. Each costs that price: the weekend surcharge is not added to it, and these nights are in neither
    /// <see cref="WeekdayNights"/> nor <see cref="WeekendNights"/>.
    /// </summary>
    public IReadOnlyList<PricedNight> PricedNights { get; init; } = [];

    /// <summary>Every night of the stay.</summary>
    public int Nights => WeekdayNights + WeekendNights + PricedNights.Count;
}

/// <summary>One night whose price the host set for its date: the Europe/Rome calendar date the night starts and its price.</summary>
public sealed record PricedNight(DateOnly Date, decimal Price);

/// <summary>
/// One tier of the length-of-stay discount of a property (NF-35): from <paramref name="MinNights"/> nights the lodging costs
/// <paramref name="PercentOff"/> percent less. The rule that picks the tier and rounds the amount is <see cref="StayPricing.DiscountFor"/>.
/// </summary>
public sealed record StayDiscountTier(int MinNights, decimal PercentOff);

/// <summary>
/// The length-of-stay discount a stay got (NF-35, ADR-028-R3, R4): the tier that applied and what it took off the nights.
/// </summary>
/// <param name="MinNights">The nights of the tier that applied.</param>
/// <param name="PercentOff">Its percentage.</param>
/// <param name="Amount">What it takes off the price of the nights: <c>round(total x percent / 100, 2)</c>, half away from zero, an exact number of cents.</param>
public sealed record StayDiscount(int MinNights, decimal PercentOff, decimal Amount);

/// <summary>What a line of the quote is. Serialized by name: never rename or reorder a member the frontend reads.</summary>
public enum QuoteLineKind
{
    /// <summary>
    /// Nights at the ordinary nightly rate (the "notti feriali"; every night when there is no weekend surcharge). There is one
    /// more line of this kind, in the order of the nights, for each other price that the host set for dates of the stay (a
    /// confirmed seasonal price): the quantity and the unit price of each line say which nights it is.
    /// </summary>
    Nights = 0,

    /// <summary>Friday and Saturday nights at the rate with the weekend surcharge; present only when there are such nights.</summary>
    WeekendNights = 1,

    /// <summary>
    /// The length-of-stay discount (NF-35): a negative amount, taken off the nights only, never off the cleaning fee or the tourist tax; present
    /// only when a tier of the property applies. It comes after the night lines and before the cleaning fee; its number is the next free one, the
    /// other members keep theirs.
    /// </summary>
    LengthOfStayDiscount = 5,

    /// <summary>The cleaning fee, once; present only when it is not zero.</summary>
    CleaningFee = 2,

    /// <summary>The tourist tax, already calculated by the tourist tax engine; present only when its amount is known.</summary>
    TouristTax = 3,

    /// <summary>The total to pay: the sum of the lines above.</summary>
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
    /// <summary>409: the host has not completed Stripe Connect: the site does not take bookings yet (R-11).</summary>
    public const string PaymentsNotReady = "direct_booking_payments_not_ready";

    /// <summary>422: check-in in the past, check-out not after check-in, or a stay longer than the longest one priced.</summary>
    public const string InvalidStay = "direct_booking_invalid_stay";

    /// <summary>400: the data processing consent was not given.</summary>
    public const string ConsentRequired = "direct_booking_consent_required";

    /// <summary>422: the consent text changed since the page was loaded.</summary>
    public const string ConsentOutdated = "direct_booking_consent_outdated";

    /// <summary>422: unknown payment option.</summary>
    public const string InvalidPaymentOption = "direct_booking_invalid_payment_option";

    /// <summary>
    /// 422: "Paga alla scadenza" chosen for a stay whose charge day is not after today (arrival too close, A3-16,
    /// <see cref="DirectBookingPaymentRules.IsDeferredPaymentOffered"/>).
    /// </summary>
    public const string DeferredPaymentUnavailable = "direct_booking_deferred_payment_unavailable";

    /// <summary>422: the tourist tax depends on the age of the minors and their ages are missing (BK-03).</summary>
    public const string ChildAgesRequired = "tourist_tax_child_ages_required";

    /// <summary>
    /// 422: the stay is shorter than the minimum stay of the property (<c>Property.MinNights</c>, DB-03), in the quote and in
    /// the checkout. The message says the minimum. The host can still enter a shorter stay by hand.
    /// </summary>
    public const string MinNightsNotMet = "direct_booking_min_nights_not_met";

    /// <summary>
    /// 422: the checkout carries <c>marketingConsent: true</c> but the marketing consent text has no version
    /// (<c>Gdpr:MarketingConsentVersion</c>, CO-15): the consent would be recorded without proof of what the guest agreed to,
    /// so it is not offered (<c>marketingConsentVersion</c> of the public org is null) and not accepted either.
    /// </summary>
    public const string MarketingConsentUnavailable = "direct_booking_marketing_consent_unavailable";

    /// <summary>
    /// 422: the checkout ticks the WhatsApp consent but the guest left no phone number that can be reached (E07/F04/US01/T02, NF-90): a consent
    /// on a channel needs the address of that channel.
    /// </summary>
    public const string WhatsAppRequiresPhone = "direct_booking_whatsapp_requires_phone";

    /// <summary>
    /// 409: the checkout carries a <c>softOptInNoticeVersion</c> that is not the current one (<c>Gdpr:SoftOptInNoticeVersion</c>, or none is
    /// configured any more): the notice the guest read is not the notice in force, so it is never recorded as presented.
    /// </summary>
    public const string SoftOptInNoticeOutdated = "direct_booking_soft_opt_in_notice_outdated";

    /// <summary>
    /// 422: the checkout carries an <c>expectedArrivalTime</c> that is neither <c>HH:mm</c> nor <c>late</c> (DB-03b,
    /// <see cref="ArrivalTimeRules"/>). The field is optional and only a preference of the guest, so nothing else is refused for it.
    /// </summary>
    public const string ArrivalTimeInvalid = "direct_booking_arrival_time_invalid";

    /// <summary>
    /// The property has a currency that is neither EUR nor CHF (<see cref="StayCurrency.IsSupported"/>): 422 from the quote and the
    /// checkout, 501 from the host's manual booking. A house in Switzerland is a CHF house and is priced, charged and refunded in francs
    /// (CHF-MONEY, which supersedes ADR-001-R10: CHF used to be refused here).
    /// </summary>
    public const string CurrencyNotSupported = "currency_not_supported";
}
