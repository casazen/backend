using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

/// <summary>
/// The tourist tax of a stay guest by guest (NF-46, ADR-037): what the check-in wizard shows and confirms, what the Octorate tax values and the
/// compliance status read. It sits next to <see cref="ITouristTaxService"/> (the rate table) and never replaces it; the booking price,
/// <c>Booking.TouristTax</c> and the check-out declaration (<c>StayCheckout.TouristTaxCollection</c>) are not touched. Callers scope the booking
/// or the property (the tenant filter and the host authorization) before calling; a row of another org is not found.
/// </summary>
public interface IStayTouristTaxService
{
    /// <summary>
    /// The tax of the stay as it is now: the guests of the booking (<see cref="IStayGuestService.GetForBookingAsync"/>, each age taken at the
    /// check-in date from the date of birth), the comune of the property, the breakdown of <see cref="ITouristTaxQuoteService.QuoteGuestsAsync"/>
    /// with the org of the booking, then <see cref="TouristTaxCollectorRules"/>. No name and no document.
    /// </summary>
    Task<StayTouristTaxView> GetAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the host's exemption of the guests in place (<see cref="StayGuest.TaxExemptionCode"/>; a null code clears it). 422
    /// <c>tax_exemption_unknown</c> for an unknown, inactive or automatic reason, 422 <c>tax_guest_unknown</c> for a position with no saved guest.
    /// A confirmed record becomes stale.
    /// </summary>
    Task<StayTouristTaxView> SetExemptionsAsync(
        Guid bookingId,
        IReadOnlyList<GuestExemption> exemptions,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The host's declaration of who collected the tax (null clears it), only for a channel booking whose comune has no channel rule; any other
    /// booking answers 422 <c>tax_collector_not_applicable</c>.
    /// </summary>
    Task<StayTouristTaxView> SetCollectorAsync(
        Guid bookingId,
        TouristTaxCollector? collector,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>The rate the org declared for the comune of the property, and whether an official rate is in force there today.</summary>
    Task<MunicipalityRateView> GetMunicipalityRateAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Declares (or replaces) the org's rate for the comune of the property (AC-NF46-6, ADR-037-R5): 409 <c>tax_rate_official_exists</c> when an
    /// official rate is in force there today, 422 <c>tax_rate_source_required</c> without a source note, 422 <c>tax_rate_invalid</c> for an amount
    /// outside 0.01 to 50, a night cap under 1, an age outside 0 to 18, a season with one bound only or a property with no comune.
    /// </summary>
    Task<MunicipalityRateView> SaveMunicipalityRateAsync(
        Guid propertyId,
        MunicipalityRateInput input,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the tax of the stay (ADR-037-R4): 422 <c>tax_not_calculable</c> while it is <see cref="TouristTaxQuoteStatus.RateUnavailable"/>,
    /// <see cref="TouristTaxQuoteStatus.ChildAgesRequired"/>, <see cref="TouristTaxQuoteStatus.CategoryRequired"/> or
    /// <see cref="TouristTaxQuoteStatus.NightlyPriceRequired"/>; otherwise replaces the <see cref="StayTouristTax"/> record and its lines
    /// (confirmation time and author, calculation hash, retention date of the check-out plus 10 years). Idempotent: a calculation already confirmed
    /// with the same hash changes nothing.
    /// </summary>
    Task<StayTouristTaxView> ConfirmAsync(Guid bookingId, string userId, CancellationToken cancellationToken = default);
}

/// <summary>The exemption the host chooses for the guest at <paramref name="Position"/>; a null <paramref name="Code"/> clears it.</summary>
public sealed record GuestExemption(int Position, string? Code);

/// <summary>Where the rates of a stay's tax come from.</summary>
public enum TouristTaxRateSource
{
    /// <summary>No rate is known for the stay (the tax step shows the sub-step to declare one; the amount is never 0).</summary>
    None = 0,

    /// <summary>The official rates of the comune.</summary>
    Official = 1,

    /// <summary>The rate the org declared for a comune with no official one.</summary>
    HostDeclared = 2,
}

/// <summary>The tax of a stay as the wizard shows it. No name, no document: positions only.</summary>
/// <param name="BookingId">The stay.</param>
/// <param name="Status">Outcome of the calculation.</param>
/// <param name="RateSource">Where the rates come from.</param>
/// <param name="MissingRate">True when no rate is known: the amounts are null, never 0.</param>
/// <param name="Nights">Nights of the stay.</param>
/// <param name="TaxableNights">Nights subject to the tax; 0 when not calculated.</param>
/// <param name="AgeRulesApply">True when the amounts depend on the age of the guests (each needs a date of birth).</param>
/// <param name="Categories">Accommodation categories of the comune, when the category is required.</param>
/// <param name="Total">Tax of the stay; null unless calculated.</param>
/// <param name="AmountDue">What the host must collect (0 when the channel collects); null unless calculated.</param>
/// <param name="Collector">Who collects the tax; null while unknown.</param>
/// <param name="CollectorSource">Where <paramref name="Collector"/> comes from.</param>
/// <param name="CollectorNeeded">A channel stay with no rule and no declaration: the host is asked who collected.</param>
/// <param name="Guests">One line per guest of the stay.</param>
/// <param name="Reasons">The exemption reasons the host may choose (active, not automatic), in order.</param>
/// <param name="ConfirmedAt">When the tax was confirmed; null when it never was.</param>
/// <param name="Stale">True when a confirmed record no longer matches the stay (rates, guests, exemptions or collector changed since).</param>
public sealed record StayTouristTaxView(
    Guid BookingId,
    TouristTaxQuoteStatus Status,
    TouristTaxRateSource RateSource,
    bool MissingRate,
    int Nights,
    int TaxableNights,
    bool AgeRulesApply,
    IReadOnlyList<string> Categories,
    decimal? Total,
    decimal? AmountDue,
    TouristTaxCollector? Collector,
    TouristTaxCollectorSource CollectorSource,
    bool CollectorNeeded,
    IReadOnlyList<StayTouristTaxGuestView> Guests,
    IReadOnlyList<TouristTaxExemptionReasonView> Reasons,
    DateTime? ConfirmedAt,
    bool Stale);

/// <summary>The tax of one guest of the stay.</summary>
/// <param name="Position">The position of the guest in the stay.</param>
/// <param name="Nights">Nights of the stay.</param>
/// <param name="TaxableNights">Nights on which the guest is taxed; 0 when not calculated.</param>
/// <param name="Amount">Tax of the guest; null when the stay's tax is not calculated (never 0 for an unknown amount).</param>
/// <param name="ExemptionKind">Why the guest pays nothing, if so.</param>
/// <param name="ExemptionCode">The host's reason, or <see cref="TouristTaxExemptionReason.MinorAgeCode"/> for the age.</param>
public sealed record StayTouristTaxGuestView(
    int Position,
    int Nights,
    int TaxableNights,
    decimal? Amount,
    TouristTaxExemptionKind ExemptionKind,
    string? ExemptionCode);

/// <summary>An exemption reason the host may choose: its code and the i18n key of its label.</summary>
public sealed record TouristTaxExemptionReasonView(string Code, string LabelKey);

/// <summary>The rate the org declared for a comune with no official rate.</summary>
public sealed record MunicipalityRateInput(
    decimal RatePerPersonPerNight,
    int? MaxNights,
    int MinimumAge,
    string? SeasonStart,
    string? SeasonEnd,
    DateOnly? EffectiveFrom,
    string? SourceNote);

/// <summary>The comune of a property for its tourist tax, and the rate the org declared for it.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="IstatCode">The ISTAT code of the comune, when the property has a trusted one.</param>
/// <param name="City">The name of the comune.</param>
/// <param name="OfficialRateInForce">True when an official rate is in force there today: no rate can be declared.</param>
/// <param name="Rate">The org's declared rate; null when none.</param>
public sealed record MunicipalityRateView(
    Guid PropertyId,
    string? IstatCode,
    string? City,
    bool OfficialRateInForce,
    OrgTouristTaxRate? Rate);
