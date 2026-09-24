using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.TouristTax;

/// <summary>Outcome of a tourist tax quote.</summary>
public enum TouristTaxQuoteStatus
{
    /// <summary>The amount is known (<see cref="TouristTaxQuote.Amount"/>, possibly 0 when every guest is exempt).</summary>
    Calculated = 0,

    /// <summary>
    /// CasaZen has no rate for the comune, or none for some night of the stay: the amount is not known and is NOT 0.
    /// The booking goes on without the tax (never an invented amount, never a blocked checkout: A8-12, R-05).
    /// </summary>
    RateUnavailable = 1,

    /// <summary>
    /// The comune's rates depend on the accommodation category (Roma, Venezia) and the stay has none, or an unknown
    /// one: see <see cref="TouristTaxQuote.Categories"/>.
    /// </summary>
    CategoryRequired = 2,

    /// <summary>The rates exempt or reduce minors by age and the ages of the minors are missing or invalid.</summary>
    ChildAgesRequired = 3,

    /// <summary>A percentage rate applies and the night price is missing.</summary>
    NightlyPriceRequired = 4,
}

/// <summary>A stay to quote.</summary>
/// <param name="CheckIn">Check-in date (Europe/Rome calendar date).</param>
/// <param name="CheckOut">Check-out date: the nights are <paramref name="CheckIn"/> .. <paramref name="CheckOut"/> - 1.</param>
/// <param name="Adults">Guests aged 18 or more: never exempt by age.</param>
/// <param name="Children">Minors (under 18).</param>
/// <param name="ChildrenAges">
/// Age of each minor at check-in (0-17), one per minor. Needed only when <see cref="TouristTaxQuote.AgeRulesApply"/>.
/// </param>
/// <param name="AccommodationCategory">Category of the accommodation, for comuni whose rates depend on it.</param>
/// <param name="NightlyPrice">Price of one night of the accommodation (cleaning and other services excluded), for percentage rates.</param>
public sealed record TouristTaxStay(
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Adults,
    int Children,
    IReadOnlyList<int>? ChildrenAges = null,
    string? AccommodationCategory = null,
    decimal? NightlyPrice = null)
{
    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;

    public int Guests => Adults + Children;
}

/// <summary>Result of <see cref="TouristTaxCalculator.Calculate"/>.</summary>
/// <param name="Status">Whether the amount is known, and if not, why.</param>
/// <param name="Amount">Tax of the whole stay in euros, rounded to the cent. Null unless <see cref="TouristTaxQuoteStatus.Calculated"/>.</param>
/// <param name="Nights">Nights of the stay.</param>
/// <param name="TaxableNights">Nights subject to the tax (the first ones, up to the cap of the rate). 0 when not calculated.</param>
/// <param name="AgeRulesApply">True when the amount depends on the age of the minors (the checkout must ask it).</param>
/// <param name="Categories">Accommodation categories of the comune, when <see cref="TouristTaxQuoteStatus.CategoryRequired"/>.</param>
/// <param name="AppliedRates">Rates used for the nights of the stay, in order of first use.</param>
public sealed record TouristTaxQuote(
    TouristTaxQuoteStatus Status,
    decimal? Amount,
    int Nights,
    int TaxableNights,
    bool AgeRulesApply,
    IReadOnlyList<string> Categories,
    IReadOnlyList<TouristTaxRate> AppliedRates)
{
    /// <summary>Amount added to the price of the stay: the tax when calculated, otherwise 0 (not included).</summary>
    public decimal AmountOrZero => Status == TouristTaxQuoteStatus.Calculated ? Amount ?? 0m : 0m;
}

/// <summary>
/// The only tourist tax calculation of CasaZen (task BK-03, defects A3-02, A5-16, A8-23): used by the checkout quote,
/// the direct and manual bookings, the activation wizard and the public calculator.
/// </summary>
/// <remarks>
/// <para>Rules, in this order:</para>
/// <list type="number">
/// <item>Each night is dated by its Europe/Rome calendar date (check-in, check-in + 1, ...). For each night the rate
/// is the active one whose validity (<c>EffectiveFrom</c>..<c>EffectiveTo</c>) and season contain that date; with an
/// accommodation category, a rate of that category wins over a rate for every accommodation; between equals, the
/// latest <c>EffectiveFrom</c>. A stay across a season change is taxed night by night.</item>
/// <item>A night without a rate makes the whole quote <see cref="TouristTaxQuoteStatus.RateUnavailable"/> (no partial
/// amount); a night whose only rates are for specific categories makes it
/// <see cref="TouristTaxQuoteStatus.CategoryRequired"/>.</item>
/// <item>Cap of nights: night number <c>n</c> (0-based from check-in) is taxed only if <c>n &lt; MaxNights</c> of its
/// rate.</item>
/// <item>Guests: adults count as 18. A guest younger than <c>MinimumAge</c> is exempt; a guest from <c>MinimumAge</c>
/// up to <c>ReducedRateMaxAge</c> included pays <c>ReducedRatePerPersonPerNight</c> (the amount published by the
/// comune, fixed rates only).</item>
/// <item>Full amount per person per night: the fixed rate, or <c>PercentOfNightlyPrice</c>% of the night price
/// divided by the number of guests (all of them, exempt included), capped at <c>CapPerPersonPerNight</c>.</item>
/// <item>Rounding: every amount per guest per night is rounded to the cent, half away from zero (0,005 → 0,01),
/// before it is added; the total is the exact sum of those cents. Only percentage rates produce fractions of a cent.</item>
/// </list>
/// </remarks>
public static class TouristTaxCalculator
{
    /// <summary>Age used for adults: they are never exempt by age (<c>MinimumAge</c> is at most 18).</summary>
    public const int AdultAge = 18;

    /// <summary>Longest stay quoted (guard for public endpoints).</summary>
    public const int MaxNights = 366;

    public static TouristTaxQuote Calculate(IEnumerable<TouristTaxRate> rates, TouristTaxStay stay)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(stay);
        if (stay.Nights <= 0)
            throw new ArgumentException("The check-out must be after the check-in.", nameof(stay));
        if (stay.Nights > MaxNights)
            throw new ArgumentException($"A stay longer than {MaxNights} nights is not quoted.", nameof(stay));
        if (stay.Adults < 0 || stay.Children < 0 || stay.Guests == 0)
            throw new ArgumentException("A stay needs at least one guest and no negative counts.", nameof(stay));

        var active = rates.Where(r => r.IsActive).ToList();
        var category = NormalizeCategory(stay.AccommodationCategory);
        var nights = Enumerable.Range(0, stay.Nights).Select(n => stay.CheckIn.AddDays(n)).ToList();
        var applicableByNight = nights
            .Select(date => active.Where(r => IsInForce(r, date) && IsInSeason(r, date)).ToList())
            .ToList();

        // Categories the comune has during the stay: a category outside this list is unknown, not merely unpriced.
        var categories = new SortedSet<string>(
            applicableByNight.SelectMany(list => list)
                .Select(r => NormalizeCategory(r.AccommodationCategory))
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);
        var knownCategory = category is not null && categories.Contains(category);

        var nightRates = new TouristTaxRate[stay.Nights];
        var unavailable = false;
        var needsCategory = false;
        for (var night = 0; night < stay.Nights; night++)
        {
            var applicable = applicableByNight[night];
            var rate = Pick(applicable, category);
            if (rate is not null)
                nightRates[night] = rate;
            else if (!knownCategory && applicable.Any(r => NormalizeCategory(r.AccommodationCategory) is not null))
                needsCategory = true;
            else
                unavailable = true;
        }

        if (unavailable)
            return NotCalculated(TouristTaxQuoteStatus.RateUnavailable, stay, ageRulesApply: false, []);
        if (needsCategory)
            return NotCalculated(TouristTaxQuoteStatus.CategoryRequired, stay, ageRulesApply: false, [.. categories]);

        var applied = nightRates.DistinctBy(r => r.Id).ToList();
        var ageRulesApply = applied.Any(AgeMatters);

        var guestAges = GuestAges(stay, ageRulesApply);
        if (guestAges is null)
            return NotCalculated(TouristTaxQuoteStatus.ChildAgesRequired, stay, ageRulesApply, [], applied);

        if (applied.Any(r => r.CalculationMethod == TouristTaxCalculationMethod.PercentOfNightlyPrice)
            && stay.NightlyPrice is not >= 0m)
        {
            return NotCalculated(TouristTaxQuoteStatus.NightlyPriceRequired, stay, ageRulesApply, [], applied);
        }

        var total = 0m;
        var taxableNights = 0;
        for (var night = 0; night < stay.Nights; night++)
        {
            var rate = nightRates[night];
            if (rate.MaxNights is int cap && night >= cap)
                continue;

            taxableNights++;
            var full = FullAmountPerPerson(rate, stay);
            foreach (var age in guestAges)
                total += AmountForGuest(rate, full, age);
        }

        return new TouristTaxQuote(
            TouristTaxQuoteStatus.Calculated,
            RoundToCent(total),
            stay.Nights,
            taxableNights,
            ageRulesApply,
            [],
            applied);
    }

    /// <summary>
    /// Active rates valid on <paramref name="date"/>, whatever their category or season: what CasaZen knows about the
    /// comune on that day (public page, sitemap).
    /// </summary>
    public static IReadOnlyList<TouristTaxRate> RatesInForce(IEnumerable<TouristTaxRate> rates, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(rates);
        return rates
            .Where(r => r.IsActive && IsInForce(r, date))
            .OrderBy(r => NormalizeCategory(r.AccommodationCategory) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SeasonStart ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(r => r.EffectiveFrom)
            .ToList();
    }

    /// <summary>
    /// Rate of a night on <paramref name="date"/> for an accommodation of <paramref name="category"/> (null: no category
    /// known, so only rates for every accommodation apply). Null when there is none.
    /// </summary>
    public static TouristTaxRate? RateFor(IEnumerable<TouristTaxRate> rates, DateOnly date, string? category = null)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var applicable = rates.Where(r => r.IsActive && IsInForce(r, date) && IsInSeason(r, date)).ToList();
        return Pick(applicable, NormalizeCategory(category));
    }

    /// <summary>True when the rate exempts or reduces some minors but not all: the ages of the minors matter.</summary>
    public static bool AgeMatters(TouristTaxRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        return (rate.MinimumAge > 0 && rate.MinimumAge < AdultAge)
            || (rate.ReducedRateMaxAge is not null && rate.ReducedRatePerPersonPerNight is not null);
    }

    /// <summary>Rounds to the cent, half away from zero (commercial rounding).</summary>
    public static decimal RoundToCent(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static bool IsInForce(TouristTaxRate rate, DateOnly date)
    {
        if (RomeCalendar.DateInRome(rate.EffectiveFrom) > date)
            return false;

        return rate.EffectiveTo is not { } to || RomeCalendar.DateInRome(to) >= date;
    }

    private static bool IsInSeason(TouristTaxRate rate, DateOnly date) =>
        TouristTaxSeason.Contains(rate.SeasonStart, rate.SeasonEnd, date);

    private static TouristTaxRate? Pick(IReadOnlyList<TouristTaxRate> applicable, string? category)
    {
        if (category is not null)
        {
            var sameCategory = Latest(applicable.Where(r =>
                string.Equals(NormalizeCategory(r.AccommodationCategory), category, StringComparison.OrdinalIgnoreCase)));
            if (sameCategory is not null)
                return sameCategory;
        }

        return Latest(applicable.Where(r => NormalizeCategory(r.AccommodationCategory) is null));
    }

    private static TouristTaxRate? Latest(IEnumerable<TouristTaxRate> rates) =>
        rates.OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.UpdatedAt).ThenBy(r => r.Id).FirstOrDefault();

    private static string? NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? null : category.Trim();

    /// <summary>
    /// Age of every guest; null when the ages of the minors are needed and missing or out of 0-17. When they are not
    /// needed, minors count as 0 (the rates then treat every minor alike).
    /// </summary>
    private static List<int>? GuestAges(TouristTaxStay stay, bool ageRulesApply)
    {
        var ages = Enumerable.Repeat(AdultAge, stay.Adults).ToList();
        var childrenAges = stay.ChildrenAges;
        var agesValid = childrenAges is not null
            && childrenAges.Count == stay.Children
            && childrenAges.All(a => a is >= 0 and < AdultAge);

        if (agesValid)
        {
            ages.AddRange(childrenAges!);
            return ages;
        }

        if (ageRulesApply && stay.Children > 0)
            return null;

        ages.AddRange(Enumerable.Repeat(0, stay.Children));
        return ages;
    }

    private static decimal FullAmountPerPerson(TouristTaxRate rate, TouristTaxStay stay)
    {
        if (rate.CalculationMethod != TouristTaxCalculationMethod.PercentOfNightlyPrice)
            return RoundToCent(rate.RatePerPersonPerNight);

        var perPerson = stay.NightlyPrice!.Value * (rate.PercentOfNightlyPrice ?? 0m) / 100m / stay.Guests;
        if (rate.CapPerPersonPerNight is { } cap)
            perPerson = Math.Min(perPerson, cap);

        return RoundToCent(perPerson);
    }

    private static decimal AmountForGuest(TouristTaxRate rate, decimal fullAmount, int age)
    {
        if (age < rate.MinimumAge)
            return 0m;

        if (rate.ReducedRateMaxAge is int reducedUpTo && age <= reducedUpTo
            && rate.ReducedRatePerPersonPerNight is { } reducedAmount
            && rate.CalculationMethod == TouristTaxCalculationMethod.PerPersonPerNight)
        {
            return RoundToCent(reducedAmount);
        }

        return fullAmount;
    }

    private static TouristTaxQuote NotCalculated(
        TouristTaxQuoteStatus status,
        TouristTaxStay stay,
        bool ageRulesApply,
        IReadOnlyList<string> categories,
        IReadOnlyList<TouristTaxRate>? applied = null) =>
        new(status, null, stay.Nights, 0, ageRulesApply, categories, applied ?? []);
}
