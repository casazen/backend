using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.TouristTax;

public enum TouristTaxQuoteStatus
{
    Calculated = 0,

    RateUnavailable = 1,

    CategoryRequired = 2,

    ChildAgesRequired = 3,

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
/// <param name="NightlyPrices">
/// The price of each night, in order (one per night), when it is not the same every night, e.g. with a weekend surcharge
/// (DB-03): a percentage rate then reads the price of its own night. Used instead of <paramref name="NightlyPrice"/>; a list
/// whose length is not the number of nights is refused.
/// </param>
public sealed record TouristTaxStay(
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Adults,
    int Children,
    IReadOnlyList<int>? ChildrenAges = null,
    string? AccommodationCategory = null,
    decimal? NightlyPrice = null,
    IReadOnlyList<decimal>? NightlyPrices = null)
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
    public const int AdultAge = 18;

    public const int MaxNights = 366;

    public static TouristTaxQuote Calculate(IEnumerable<TouristTaxRate> rates, TouristTaxStay stay)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(stay);
        EnsureQuotableNights(stay.Nights, stay.NightlyPrices, nameof(stay));
        if (stay.Adults < 0 || stay.Children < 0 || stay.Guests == 0)
            throw new ArgumentException("A stay needs at least one guest and no negative counts.", nameof(stay));

        var resolved = ResolveNightRates(rates, stay.CheckIn, stay.Nights, stay.AccommodationCategory);
        if (resolved.Failure is { } failure)
            return NotCalculated(failure, stay, ageRulesApply: false, resolved.Categories);

        var applied = resolved.Applied;
        var ageRulesApply = applied.Any(AgeMatters);

        var guestAges = GuestAges(stay, ageRulesApply);
        if (guestAges is null)
            return NotCalculated(TouristTaxQuoteStatus.ChildAgesRequired, stay, ageRulesApply, [], applied);

        if (NeedsNightPrices(applied) && !HasNightPrices(stay.NightlyPrice, stay.NightlyPrices))
            return NotCalculated(TouristTaxQuoteStatus.NightlyPriceRequired, stay, ageRulesApply, [], applied);

        var guests = guestAges.Select((age, index) => new GuestOnNights(index, age, HostExemptionCode: null)).ToList();
        var taxed = TaxNights(resolved.NightRates, guests, stay.NightlyPrice, stay.NightlyPrices);

        return new TouristTaxQuote(
            TouristTaxQuoteStatus.Calculated,
            RoundToCent(taxed.Total),
            stay.Nights,
            taxed.TaxableNights,
            ageRulesApply,
            [],
            applied);
    }

    /// <summary>
    /// The tax of <paramref name="stay"/> guest by guest and night by night (NF-46, ADR-037-R2), with the rules of <see cref="Calculate"/> and
    /// the same loop, so the total of the guests is the total <see cref="Calculate"/> gives for the same stay without host exemptions.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A guest younger than the <c>MinimumAge</c> of the night's rate pays nothing that night; one exempt on every taxed night has the kind
    /// <see cref="TouristTaxExemptionKind.Age"/> (the automatic <see cref="TouristTaxExemptionReason.MinorAgeCode"/>).</item>
    /// <item>A guest with a host exemption pays 0 on every night (kind <see cref="TouristTaxExemptionKind.Host"/>) and needs no age; the
    /// others keep their amounts.</item>
    /// <item>A percentage rate is still divided by ALL the guests, exempt ones included (the rule of <see cref="Calculate"/>).</item>
    /// <item>When the rates depend on age (an exemption age above 0 or a reduced band), a guest with no age makes the stay
    /// <see cref="TouristTaxQuoteStatus.ChildAgesRequired"/>: never an assumed adult.</item>
    /// <item>Statuses and rounding (half away from zero, per guest and night) are the ones of <see cref="Calculate"/>.</item>
    /// </list>
    /// </remarks>
    public static TouristTaxBreakdown CalculatePerGuest(IEnumerable<TouristTaxRate> rates, TouristTaxGuestStay stay)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(stay);
        ArgumentNullException.ThrowIfNull(stay.Guests);
        EnsureQuotableNights(stay.Nights, stay.NightlyPrices, nameof(stay));
        if (stay.Guests.Count == 0)
            throw new ArgumentException("A stay needs at least one guest.", nameof(stay));
        if (stay.Guests.Select(g => g.Position).Distinct().Count() != stay.Guests.Count)
            throw new ArgumentException("Every guest of the stay needs its own position.", nameof(stay));

        var resolved = ResolveNightRates(rates, stay.CheckIn, stay.Nights, stay.AccommodationCategory);
        if (resolved.Failure is { } failure)
            return NotCalculated(failure, stay, ageRulesApply: false, resolved.Categories);

        var applied = resolved.Applied;
        var ageRulesApply = applied.Any(AgeMattersPerGuest);

        var guests = new List<GuestOnNights>(stay.Guests.Count);
        foreach (var guest in stay.Guests)
        {
            var hostExemption = string.IsNullOrWhiteSpace(guest.HostExemptionCode) ? null : guest.HostExemptionCode.Trim();
            var knownAge = guest.AgeAtCheckIn is >= 0 ? guest.AgeAtCheckIn : null;
            if (knownAge is null && hostExemption is null && ageRulesApply)
                return NotCalculated(TouristTaxQuoteStatus.ChildAgesRequired, stay, ageRulesApply, [], applied);

            guests.Add(new GuestOnNights(guest.Position, knownAge ?? AdultAge, hostExemption));
        }

        if (NeedsNightPrices(applied) && !HasNightPrices(stay.NightlyPrice, stay.NightlyPrices))
            return NotCalculated(TouristTaxQuoteStatus.NightlyPriceRequired, stay, ageRulesApply, [], applied);

        var taxed = TaxNights(resolved.NightRates, guests, stay.NightlyPrice, stay.NightlyPrices);
        var results = guests
            .Select((guest, index) => GuestResult(guest, index, stay.CheckIn, resolved.NightRates, taxed))
            .ToList();

        return new TouristTaxBreakdown(
            TouristTaxQuoteStatus.Calculated,
            RoundToCent(taxed.Total),
            stay.Nights,
            taxed.TaxableNights,
            ageRulesApply,
            [],
            applied,
            results);
    }

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

    public static TouristTaxRate? RateFor(IEnumerable<TouristTaxRate> rates, DateOnly date, string? category = null)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var applicable = rates.Where(r => r.IsActive && IsInForce(r, date) && IsInSeason(r, date)).ToList();
        return Pick(applicable, NormalizeCategory(category));
    }

    public static bool AgeMatters(TouristTaxRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        return (rate.MinimumAge > 0 && rate.MinimumAge < AdultAge)
            || (rate.ReducedRateMaxAge is not null && rate.ReducedRatePerPersonPerNight is not null);
    }

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

    private static bool AgeMattersPerGuest(TouristTaxRate rate) =>
        rate.MinimumAge > 0 || (rate.ReducedRateMaxAge is not null && rate.ReducedRatePerPersonPerNight is not null);

    private static void EnsureQuotableNights(int nights, IReadOnlyList<decimal>? nightlyPrices, string paramName)
    {
        if (nights <= 0)
            throw new ArgumentException("The check-out must be after the check-in.", paramName);
        if (nights > MaxNights)
            throw new ArgumentException($"A stay longer than {MaxNights} nights is not quoted.", paramName);
        if (nightlyPrices is { } prices && prices.Count != nights)
            throw new ArgumentException("The night prices must be one per night of the stay.", paramName);
    }

    private static ResolvedNights ResolveNightRates(
        IEnumerable<TouristTaxRate> rates,
        DateOnly checkIn,
        int nightCount,
        string? accommodationCategory)
    {
        var active = rates.Where(r => r.IsActive).ToList();
        var category = NormalizeCategory(accommodationCategory);
        var nights = Enumerable.Range(0, nightCount).Select(n => checkIn.AddDays(n)).ToList();
        var applicableByNight = nights
            .Select(date => active.Where(r => IsInForce(r, date) && IsInSeason(r, date)).ToList())
            .ToList();

        var categories = new SortedSet<string>(
            applicableByNight.SelectMany(list => list)
                .Select(r => NormalizeCategory(r.AccommodationCategory))
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);
        var knownCategory = category is not null && categories.Contains(category);

        var nightRates = new TouristTaxRate[nightCount];
        var unavailable = false;
        var needsCategory = false;
        for (var night = 0; night < nightCount; night++)
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
            return new ResolvedNights(TouristTaxQuoteStatus.RateUnavailable, [], [], []);
        if (needsCategory)
            return new ResolvedNights(TouristTaxQuoteStatus.CategoryRequired, [.. categories], [], []);

        return new ResolvedNights(null, [], nightRates, nightRates.DistinctBy(r => r.Id).ToList());
    }

    private static bool NeedsNightPrices(IEnumerable<TouristTaxRate> applied) =>
        applied.Any(r => r.CalculationMethod == TouristTaxCalculationMethod.PercentOfNightlyPrice);

    private static bool HasNightPrices(decimal? nightlyPrice, IReadOnlyList<decimal>? nightlyPrices) =>
        nightlyPrices is { } prices ? prices.All(price => price >= 0m) : nightlyPrice is >= 0m;

    private static decimal PriceOfNight(decimal? nightlyPrice, IReadOnlyList<decimal>? nightlyPrices, int night) =>
        nightlyPrices is { } prices ? prices[night] : nightlyPrice ?? 0m;

    private static decimal FullAmountPerPerson(TouristTaxRate rate, int guestCount, decimal nightPrice)
    {
        if (rate.CalculationMethod != TouristTaxCalculationMethod.PercentOfNightlyPrice)
            return RoundToCent(rate.RatePerPersonPerNight);

        var perPerson = nightPrice * (rate.PercentOfNightlyPrice ?? 0m) / 100m / guestCount;
        if (rate.CapPerPersonPerNight is { } cap)
            perPerson = Math.Min(perPerson, cap);

        return RoundToCent(perPerson);
    }

    private static NightsTaxed TaxNights(
        IReadOnlyList<TouristTaxRate> nightRates,
        IReadOnlyList<GuestOnNights> guests,
        decimal? nightlyPrice,
        IReadOnlyList<decimal>? nightlyPrices)
    {
        var amounts = guests.Select(_ => new decimal[nightRates.Count]).ToArray();
        var withinCap = new bool[nightRates.Count];
        var total = 0m;
        var taxableNights = 0;
        for (var night = 0; night < nightRates.Count; night++)
        {
            var rate = nightRates[night];
            if (rate.MaxNights is int cap && night >= cap)
                continue;

            withinCap[night] = true;
            taxableNights++;
            var full = FullAmountPerPerson(rate, guests.Count, PriceOfNight(nightlyPrice, nightlyPrices, night));
            for (var index = 0; index < guests.Count; index++)
            {
                var guest = guests[index];
                var amount = guest.HostExemptionCode is null ? AmountForGuest(rate, full, guest.Age) : 0m;
                amounts[index][night] = amount;
                total += amount;
            }
        }

        return new NightsTaxed(total, taxableNights, amounts, withinCap);
    }

    private static TouristTaxGuestResult GuestResult(
        GuestOnNights guest,
        int index,
        DateOnly checkIn,
        IReadOnlyList<TouristTaxRate> nightRates,
        NightsTaxed taxed)
    {
        var nights = new List<TouristTaxNightAmount>(nightRates.Count);
        var taxedNights = 0;
        var ageExemptNights = 0;
        var nightsWithinCap = 0;
        for (var night = 0; night < nightRates.Count; night++)
        {
            nights.Add(new TouristTaxNightAmount(checkIn.AddDays(night), taxed.Amounts[index][night], nightRates[night].Id));
            if (!taxed.WithinCap[night])
                continue;

            nightsWithinCap++;
            if (guest.HostExemptionCode is not null)
                continue;
            if (guest.Age < nightRates[night].MinimumAge)
                ageExemptNights++;
            else
                taxedNights++;
        }

        var kind = TouristTaxExemptionKind.None;
        string? code = null;
        if (guest.HostExemptionCode is not null)
        {
            kind = TouristTaxExemptionKind.Host;
            code = guest.HostExemptionCode;
        }
        else if (nightsWithinCap > 0 && ageExemptNights == nightsWithinCap)
        {
            kind = TouristTaxExemptionKind.Age;
            code = TouristTaxExemptionReason.MinorAgeCode;
        }

        return new TouristTaxGuestResult(
            guest.Position,
            RoundToCent(nights.Sum(n => n.Amount)),
            taxedNights,
            kind,
            code,
            nights);
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

    private static TouristTaxBreakdown NotCalculated(
        TouristTaxQuoteStatus status,
        TouristTaxGuestStay stay,
        bool ageRulesApply,
        IReadOnlyList<string> categories,
        IReadOnlyList<TouristTaxRate>? applied = null) =>
        new(status, null, stay.Nights, 0, ageRulesApply, categories, applied ?? [], []);

    private sealed record GuestOnNights(int Position, int Age, string? HostExemptionCode);

    private sealed record ResolvedNights(
        TouristTaxQuoteStatus? Failure,
        IReadOnlyList<string> Categories,
        IReadOnlyList<TouristTaxRate> NightRates,
        IReadOnlyList<TouristTaxRate> Applied);

    private sealed record NightsTaxed(decimal Total, int TaxableNights, decimal[][] Amounts, bool[] WithinCap);
}
