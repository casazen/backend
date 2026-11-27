using Casazen.Core.Entities;

namespace Casazen.Core.TouristTax;

/// <summary>A guest of a stay whose tourist tax is calculated guest by guest (NF-46). No name, no document: a position and an age.</summary>
/// <param name="Position">The position of the guest in the stay (the <c>StayGuest.Position</c>), unique in the stay.</param>
/// <param name="AgeAtCheckIn">Age at the check-in date; null when not known (then the stay is <see cref="TouristTaxQuoteStatus.ChildAgesRequired"/>
/// if the rates depend on age, never an assumed adult).</param>
/// <param name="HostExemptionCode">The exemption the host chose for the guest (a non-automatic <see cref="TouristTaxExemptionReason"/>); null: none.</param>
public sealed record TouristTaxGuest(int Position, int? AgeAtCheckIn, string? HostExemptionCode = null);

/// <summary>A stay to calculate guest by guest: the same inputs as <see cref="TouristTaxStay"/>, with the guests one by one.</summary>
/// <param name="CheckIn">Check-in date (Europe/Rome calendar date).</param>
/// <param name="CheckOut">Check-out date: the nights are <paramref name="CheckIn"/> .. <paramref name="CheckOut"/> - 1.</param>
/// <param name="Guests">Every guest of the stay, exempt ones included (a percentage rate is divided by all of them).</param>
/// <param name="AccommodationCategory">Category of the accommodation, for comuni whose rates depend on it.</param>
/// <param name="NightlyPrice">Price of one night, for percentage rates.</param>
/// <param name="NightlyPrices">The price of each night, in order, when it differs (see <see cref="TouristTaxStay.NightlyPrices"/>).</param>
public sealed record TouristTaxGuestStay(
    DateOnly CheckIn,
    DateOnly CheckOut,
    IReadOnlyList<TouristTaxGuest> Guests,
    string? AccommodationCategory = null,
    decimal? NightlyPrice = null,
    IReadOnlyList<decimal>? NightlyPrices = null)
{
    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;
}

/// <summary>The tax of one guest on one night of the stay.</summary>
/// <param name="Date">The night (Europe/Rome calendar date).</param>
/// <param name="Amount">Tax of the guest on that night, rounded to the cent; 0 beyond the cap of nights or for an exempt guest.</param>
/// <param name="RateId">The rate of that night.</param>
public sealed record TouristTaxNightAmount(DateOnly Date, decimal Amount, Guid RateId);

/// <summary>The tax of one guest of the stay.</summary>
/// <param name="Position">The position of the guest.</param>
/// <param name="Amount">Tax of the guest over the stay (the sum of <paramref name="Nights"/>).</param>
/// <param name="TaxableNights">Nights on which the guest was taxed (within the cap, not exempt).</param>
/// <param name="ExemptionKind"><see cref="TouristTaxExemptionKind.Host"/> for a host exemption; <see cref="TouristTaxExemptionKind.Age"/> when the
/// guest is exempt by age on every night within the cap; <see cref="TouristTaxExemptionKind.None"/> otherwise.</param>
/// <param name="ExemptionCode">The host's reason, or <see cref="TouristTaxExemptionReason.MinorAgeCode"/> for the age; null for a paying guest.</param>
/// <param name="Nights">Every night of the stay, in order.</param>
public sealed record TouristTaxGuestResult(
    int Position,
    decimal Amount,
    int TaxableNights,
    TouristTaxExemptionKind ExemptionKind,
    string? ExemptionCode,
    IReadOnlyList<TouristTaxNightAmount> Nights);

/// <summary>Result of <see cref="TouristTaxCalculator.CalculatePerGuest"/>: the quote of the stay, guest by guest.</summary>
/// <param name="Status">Whether the amounts are known, and if not, why (the statuses of <see cref="TouristTaxCalculator.Calculate"/>).</param>
/// <param name="Total">Tax of the whole stay, the exact sum of the guests; null unless <see cref="TouristTaxQuoteStatus.Calculated"/>.</param>
/// <param name="Nights">Nights of the stay.</param>
/// <param name="TaxableNights">Nights subject to the tax (the first ones, up to the cap of the rate). 0 when not calculated.</param>
/// <param name="AgeRulesApply">True when the amount depends on the age of the guests (every guest needs an age).</param>
/// <param name="Categories">Accommodation categories of the comune, when <see cref="TouristTaxQuoteStatus.CategoryRequired"/>.</param>
/// <param name="AppliedRates">Rates used for the nights of the stay, in order of first use.</param>
/// <param name="Guests">One result per guest, in the order of the input; empty unless <see cref="TouristTaxQuoteStatus.Calculated"/>.</param>
public sealed record TouristTaxBreakdown(
    TouristTaxQuoteStatus Status,
    decimal? Total,
    int Nights,
    int TaxableNights,
    bool AgeRulesApply,
    IReadOnlyList<string> Categories,
    IReadOnlyList<TouristTaxRate> AppliedRates,
    IReadOnlyList<TouristTaxGuestResult> Guests);
