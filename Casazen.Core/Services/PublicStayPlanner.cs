using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.SiteProfile;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

public sealed record PublicStayHouse(
    Guid Id,
    string? Slug,
    string Name,
    string City,
    string? ComuneIstatCode,
    decimal Latitude,
    decimal Longitude,
    IReadOnlyList<string> Photos,
    int Bedrooms,
    int Bathrooms,
    int MaxGuests,
    int? AreaSqm,
    decimal NightlyRate,
    decimal CleaningFee,
    decimal WeekendSurchargePercent,
    int? MinNights,
    PetsPolicy? Pets,
    SmokingPolicy? Smoking,
    PartiesPolicy? Parties,
    IReadOnlyList<PropertyAmenity> Amenities,
    LocalizedText? Zone = null,
    IReadOnlyList<string>? Labels = null,
    bool? ChildrenWelcome = null,
    CancellationPolicy? CatalogPolicy = null,
    HostCancellationOverrides Overrides = default,
    string? OrgSlug = null,
    string? HostName = null,
    string Currency = PublicStayHouse.DefaultCurrency,
    string Country = PublicStayHouse.DefaultCountry,
    string Timezone = PublicStayHouse.DefaultTimezone)
{
    public const string DefaultCurrency = "EUR";

    public const string DefaultCountry = "IT";

    public const string DefaultTimezone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId;

    public IReadOnlyList<string> Tags => Labels ?? [];

    public bool AcceptsPets => Pets is { } rule ? rule != PetsPolicy.No : Amenities.Contains(PropertyAmenity.PetFriendly);

    public bool AllowsSmoking => Smoking is { } rule ? rule == SmokingPolicy.Yes : Amenities.Contains(PropertyAmenity.SmokingAllowed);

    public bool AllowsParties => Parties == PartiesPolicy.OnRequest;

    public bool HasSelfCheckIn => Amenities.Contains(PropertyAmenity.SelfCheckIn);
}

/// <summary>
/// What the search reads about the houses of one site, loaded for all of them together: the taken nights, the prices the hosts set, the rules
/// of the nights, the tourist tax rates of the period and the ratings.
/// </summary>
/// <param name="Today">Today in Europe/Rome.</param>
/// <param name="AcceptsBookings">The host can take a booking on the site (<see cref="Org.CanTakeDirectPayments"/>).</param>
/// <param name="Taken">The taken nights of each house (<see cref="PublicOccupancyReader"/> in the Infrastructure).</param>
/// <param name="HostPrices">
/// The prices the host set for dates, of each house, for many houses at once: the seasonal prices it confirmed, overridden by the prices written
/// in its calendar (<see cref="DayRulePricing.HostPrices"/>, the same prices <see cref="IPricingAdapterService.GetAppliedNightlyPricesAsync"/>
/// gives the quote of one house).
/// </param>
/// <param name="TaxRates">The active tourist tax rates of every comune that overlap the stay: the planner picks the ones of each house.</param>
/// <param name="Ratings">The public rating of each house that has reviews.</param>
/// <param name="NowUtc">
/// The instant the search is worked out at (UTC). The advance rules of the nights count the days from «today» on the clock of each house
/// (<see cref="PublicStayHouse.Timezone"/>, <see cref="DayRuleRules.TodayOf"/>), as the quote and the checkout do; <c>null</c> (a caller that did not say) counts them
/// from <paramref name="Today"/>, the calendar of Rome, for every house.
/// </param>
/// <param name="DayRules">
/// The rules of the nights of each house that has any (E08/F05/US01/T04, NF-36), by date: the closed arrival and departure, the minimum and the
/// maximum stay, the advance. The planner judges a stay with <see cref="DayRuleStayChecks"/>, the check of the quote and the checkout, so a
/// house it offers is a house the checkout accepts. <c>null</c> (or a house with no entry) means no rule: the minimum stay of the house alone.
/// </param>
public sealed record PublicStayData(
    DateOnly Today,
    bool AcceptsBookings,
    IReadOnlyDictionary<Guid, IReadOnlySet<DateOnly>> Taken,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>> HostPrices,
    IReadOnlyList<TouristTaxRate> TaxRates,
    IReadOnlyDictionary<Guid, PublicRatingSummary> Ratings,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>? DayRules = null,
    DateTime? NowUtc = null);

public static class PublicStayPlanner
{
    public static string CurrencyOf(IEnumerable<PublicStayHouse> houses)
    {
        var currencies = houses.Select(house => house.Currency).Distinct(StringComparer.Ordinal).Take(2).ToList();
        return currencies.Count == 1 ? currencies[0] : PublicStayHouse.DefaultCurrency;
    }

    private const int EstimatedChildAge = TouristTaxCalculator.AdultAge - 1;

    private static readonly int[] AlternativeOffsets = [1, -1, 2, -2, 3, -3, 4, 5, 6, 7, 10, 14, 21];

    private static readonly PropertyAmenity[] MainAmenities =
    [
        PropertyAmenity.Pool, PropertyAmenity.BeachAccess, PropertyAmenity.Terrace, PropertyAmenity.Garden, PropertyAmenity.Balcony,
        PropertyAmenity.AirConditioning, PropertyAmenity.WiFi, PropertyAmenity.FreeParking, PropertyAmenity.PetFriendly,
        PropertyAmenity.Washer, PropertyAmenity.Kitchen,
    ];

    private const int MaxCardAmenities = 6;

    private static readonly IReadOnlySet<DateOnly> NoNights = new HashSet<DateOnly>();

    private static readonly CompareInfo Italian = CultureInfo.GetCultureInfo("it-IT").CompareInfo;

    public static string DestinationId(string? city) => TouristTaxComune.NormalizeName(city).Replace(' ', '-');

    public static PublicStaysResult Search(
        IReadOnlyList<PublicStayHouse> houses,
        PublicStayData data,
        PublicStaysCriteria criteria,
        DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(houses);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(criteria);

        var unsupported = new List<string>(criteria.IgnoredValues);
        var rules = criteria.Rules;

        var amenities = criteria.Amenities
            .Select(name => Enum.TryParse<PropertyAmenity>(name, ignoreCase: true, out var amenity) ? (PropertyAmenity?)amenity : null)
            .OfType<PropertyAmenity>()
            .ToList();
        var pool = houses.Where(house => PassesFilters(house, criteria, amenities, rules)).ToList();

        var hasDates = criteria.CheckIn is not null && criteria.CheckOut is not null;
        var checkIn = criteria.CheckIn ?? default;
        var checkOut = criteria.CheckOut ?? default;
        var nights = hasDates ? checkOut.DayNumber - checkIn.DayNumber : 0;
        var rateCache = new Dictionary<(string?, string), IReadOnlyList<TouristTaxRate>>();

        var cards = new List<PublicStayCard>(pool.Count);
        foreach (var house in pool)
        {
            var card = hasDates
                ? CardForStay(house, data, criteria, checkIn, checkOut, nights, rateCache)
                : CardWithoutDates(house, data);
            if (PassesPrice(card, criteria))
                cards.Add(card);
        }

        var platform = criteria.Scope == PublicStaysScope.Platform;
        var ordered = Order(cards, criteria.Sort, hasDates, platform);
        var availableCount = cards.Count(card => card.Availability.Available);
        var pageSize = Math.Clamp(
            criteria.PageSize ?? (platform ? PublicSiteSearch.DefaultPlatformPageSize : PublicSiteSearch.MaxPageSize),
            1,
            PublicSiteSearch.MaxPageSize);
        var page = Math.Max(criteria.Page, 1);
        var listed = platform ? ordered.Where(card => card.Availability.Available) : ordered;
        var items = listed.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var kept = cards.Select(card => card.Id).ToHashSet();
        var alternatives = hasDates && availableCount == 0
            ? Alternatives(pool.Where(house => kept.Contains(house.Id)).ToList(), data, criteria, checkIn, nights)
            : [];

        return new PublicStaysResult(
            CurrencyOf(pool),
            hasDates ? checkIn : null,
            hasDates ? checkOut : null,
            hasDates ? nights : null,
            criteria.Adults,
            criteria.Children,
            criteria.Pets,
            cards.Count,
            availableCount,
            page,
            pageSize,
            items,
            alternatives,
            unsupported,
            generatedAt,
            platform ? Facets(InPlace(houses, criteria.Where, lenient: true)) : null);
    }

    public static IReadOnlyList<PublicStayHouse> InPlace(IReadOnlyList<PublicStayHouse> houses, string? place, bool lenient)
    {
        ArgumentNullException.ThrowIfNull(houses);

        return string.IsNullOrWhiteSpace(place) ? houses : houses.Where(house => MatchesPlace(house, place, lenient)).ToList();
    }

    private static PublicStaysFacets Facets(IReadOnlyList<PublicStayHouse> houses)
    {
        var rates = houses.Select(house => StayPricing.ToCents(house.NightlyRate)).ToList();
        var areas = houses.Where(house => house.AreaSqm is > 0).Select(house => house.AreaSqm!.Value).ToList();
        var tags = houses
            .SelectMany(house => house.Tags.Distinct())
            .GroupBy(tag => tag)
            .ToDictionary(group => group.Key, group => group.Count());

        return new PublicStaysFacets(
            rates.Count > 0 ? rates.Min() : null,
            rates.Count > 0 ? rates.Max() : null,
            houses.Count > 0 ? houses.Max(house => house.MaxGuests) : 0,
            areas.Count > 0 ? areas.Min() : null,
            areas.Count > 0 ? areas.Max() : null,
            houses.Any(house => house.AcceptsPets),
            SitePropertyProfileLimits.Tags.Where(tags.ContainsKey).Select(tag => new PublicStayFacetTag(tag, tags[tag])).ToList(),
            CurrencyOf(houses));
    }

    private static bool PassesFilters(
        PublicStayHouse house,
        PublicStaysCriteria criteria,
        IReadOnlyList<PropertyAmenity> amenities,
        IReadOnlyList<string> rules)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Where) && !MatchesPlace(house, criteria.Where, criteria.Scope == PublicStaysScope.Platform))
            return false;
        if (criteria.Bedrooms is { } bedrooms && house.Bedrooms < bedrooms)
            return false;
        if (criteria.Bathrooms is { } bathrooms && house.Bathrooms < bathrooms)
            return false;
        if (criteria.MinSqm is { } sqm && (house.AreaSqm is not { } area || area < sqm))
            return false;
        if (!amenities.All(house.Amenities.Contains))
            return false;
        if (!criteria.Tags.All(house.Tags.Contains))
            return false;

        return rules.All(rule => rule switch
        {
            "pets" => house.AcceptsPets,
            "selfCheckin" => house.HasSelfCheckIn,
            "smoking" => house.AllowsSmoking,
            "parties" => house.AllowsParties,
            "children" => house.ChildrenWelcome == true,
            _ => true,
        });
    }

    private const int MinCityLengthForPhrase = 4;

    private static readonly IReadOnlyDictionary<string, string> CountryNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["italia"] = "IT",
        ["italy"] = "IT",
        ["svizzera"] = "CH",
        ["switzerland"] = "CH",
        ["schweiz"] = "CH",
        ["suisse"] = "CH",
        ["svizra"] = "CH",
    };

    private static bool MatchesPlace(PublicStayHouse house, string place, bool lenient)
    {
        var wanted = TouristTaxComune.NormalizeName(place).Replace(' ', '-');
        var city = DestinationId(house.City);
        if (city == wanted)
            return true;
        if (!lenient || wanted.Length == 0)
            return false;

        if (CountryNames.TryGetValue(wanted, out var country))
            return string.Equals(house.Country, country, StringComparison.OrdinalIgnoreCase);

        return city.Contains(wanted, StringComparison.Ordinal)
            || (city.Length >= MinCityLengthForPhrase && wanted.Contains(city, StringComparison.Ordinal))
            || ZoneIds(house).Any(zone => zone.Contains(wanted, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ZoneIds(PublicStayHouse house)
    {
        if (house.Zone is not { } zone)
            yield break;

        foreach (var text in new[] { zone.It, zone.En })
        {
            var id = DestinationId(text);
            if (id.Length > 0)
                yield return id;
        }
    }

    private static bool PassesPrice(PublicStayCard card, PublicStaysCriteria criteria)
    {
        var perNight = card.Price.PerNightCents;
        return (criteria.MinPrice is not { } min || perNight >= StayPricing.ToCents(min))
            && (criteria.MaxPrice is not { } max || perNight <= StayPricing.ToCents(max));
    }

    private static PublicStayCard CardForStay(
        PublicStayHouse house,
        PublicStayData data,
        PublicStaysCriteria criteria,
        DateOnly checkIn,
        DateOnly checkOut,
        int nights,
        Dictionary<(string?, string), IReadOnlyList<TouristTaxRate>> rateCache)
    {
        var taken = TakenOf(data, house.Id);
        var hostPrices = HostPricesOf(data, house.Id);
        var rules = RulesOf(data, house.Id);
        var party = criteria.Adults + criteria.Children;

        var todayOfHouse = TodayOf(data, house);
        var violation = DayRuleStayChecks.FirstViolation(checkIn, checkOut, todayOfHouse, house.MinNights, rules);
        var reason = ReasonNotFree(house, data, criteria, party, checkIn, checkOut, taken, violation);
        DateOnly? firstFree = reason switch
        {
            null => checkIn,
            PublicStayReasons.Busy
                or PublicStayReasons.MinNights
                or PublicStayReasons.ArrivalClosed
                or PublicStayReasons.DepartureClosed
                or PublicStayReasons.MaxNights
                or PublicStayReasons.Advance => NextFreeCheckIn(
                    taken, checkIn < data.Today ? data.Today : checkIn, nights, house, rules, todayOfHouse),
            _ => null,
        };

        var lodging = StayPricing.Lodging(checkIn, nights, house.NightlyRate, house.WeekendSurchargePercent, hostPrices);
        long? total = null;
        string? includes = null;

        var quotable = checkIn >= data.Today
            && nights <= TouristTaxCalculator.MaxNights
            && party <= house.MaxGuests
            && violation is null;
        if (quotable)
        {
            var tax = TouristTaxOf(house, data, rateCache, checkIn, checkOut, criteria, lodging);
            total = StayPricing.ToCents(lodging.Total + house.CleaningFee + tax.AmountOrZero);
            includes = tax.Status != TouristTaxQuoteStatus.Calculated
                ? PublicStayTaxStates.NotIncluded
                : criteria.Children > 0 && tax.AgeRulesApply ? PublicStayTaxStates.Estimated : PublicStayTaxStates.Exact;
        }

        var cancellation = house.CatalogPolicy is null
            ? null
            : PublicCancellation.StayOf(
                CancellationRefundPolicy.EffectivePolicy(house.CatalogPolicy, house.Overrides, nights),
                checkIn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                data.Today);

        var minNightsOfArrival = rules.TryGetValue(checkIn, out var onArrival) && onArrival.MinNights is { } ruled ? ruled : house.MinNights;
        return Card(
            house,
            data,
            new PublicStayAvailability(reason is null, reason, minNightsOfArrival, firstFree),
            new PublicStayPrice(house.Currency, StayPricing.ToCents(lodging.Total / nights), total, includes),
            cancellation);
    }

    private static string? ReasonNotFree(
        PublicStayHouse house,
        PublicStayData data,
        PublicStaysCriteria criteria,
        int party,
        DateOnly checkIn,
        DateOnly checkOut,
        IReadOnlySet<DateOnly> taken,
        DayRuleViolation? violation)
    {
        if (checkIn < data.Today)
            return PublicStayReasons.Past;
        if (!data.AcceptsBookings)
            return PublicStayReasons.NotBookable;
        if (party > house.MaxGuests)
            return PublicStayReasons.Guests;
        if (criteria.Pets && !house.AcceptsPets)
            return PublicStayReasons.Pets;
        if (AnyTaken(taken, checkIn, checkOut))
            return PublicStayReasons.Busy;

        return violation is null ? null : ReasonOf(violation);
    }

    private static string ReasonOf(DayRuleViolation violation) => violation.Kind switch
    {
        DayRuleViolationKind.ArrivalClosed => PublicStayReasons.ArrivalClosed,
        DayRuleViolationKind.DepartureClosed => PublicStayReasons.DepartureClosed,
        DayRuleViolationKind.MinNights => PublicStayReasons.MinNights,
        DayRuleViolationKind.MaxNights => PublicStayReasons.MaxNights,
        _ => PublicStayReasons.Advance,
    };

    private static TouristTaxQuote TouristTaxOf(
        PublicStayHouse house,
        PublicStayData data,
        Dictionary<(string?, string), IReadOnlyList<TouristTaxRate>> rateCache,
        DateOnly checkIn,
        DateOnly checkOut,
        PublicStaysCriteria criteria,
        StayLodging lodging)
    {
        var comune = new TouristTaxComune(house.ComuneIstatCode, house.City);
        if (comune.IsEmpty)
            return TouristTaxCalculator.Calculate([], new TouristTaxStay(checkIn, checkOut, criteria.Adults, criteria.Children));

        var key = (house.ComuneIstatCode, TouristTaxComune.NormalizeName(house.City));
        if (!rateCache.TryGetValue(key, out var rates))
            rateCache[key] = rates = data.TaxRates.Where(comune.Matches).ToList();

        IReadOnlyList<int>? ages = criteria.Children > 0 ? Enumerable.Repeat(EstimatedChildAge, criteria.Children).ToList() : null;
        return TouristTaxCalculator.Calculate(
            rates,
            new TouristTaxStay(
                checkIn,
                checkOut,
                criteria.Adults,
                criteria.Children,
                ages,
                AccommodationCategory: null,
                NightlyPrice: house.NightlyRate,
                NightlyPrices: StayPricing.NightPrices(checkIn, lodging)));
    }

    /// <summary>
    /// The first day, from <paramref name="after"/> on, a stay as long as the one asked (and at least the minimum stay of the night it starts
    /// on) can start in the next <see cref="PublicSiteSearch.FirstFreeHorizonDays"/> days, or <c>null</c> (<c>nextFree</c> of the design). A
    /// day is proposed only when no night of the stay is taken <b>and</b> the stay breaks no rule of its nights (<see cref="DayRuleStayChecks"/>,
    /// NF-36): the first free date never falls on a day the checkout would refuse.
    /// </summary>
    private static DateOnly? NextFreeCheckIn(
        IReadOnlySet<DateOnly> taken,
        DateOnly after,
        int nights,
        PublicStayHouse house,
        IReadOnlyDictionary<DateOnly, DayRuleValues> rules,
        DateOnly today)
    {
        for (var offset = 0; offset < PublicSiteSearch.FirstFreeHorizonDays; offset++)
        {
            var start = after.AddDays(offset);
            rules.TryGetValue(start, out var onStart);
            var end = start.AddDays(Math.Max(nights, DayRuleDefaults.MinNightsOf(onStart, house.MinNights)));
            if (!AnyTaken(taken, start, end)
                && DayRuleStayChecks.FirstViolation(start, end, today, house.MinNights, rules) is null)
            {
                return start;
            }
        }

        return null;
    }

    private static PublicStayCard CardWithoutDates(PublicStayHouse house, PublicStayData data) =>
        Card(
            house,
            data,
            new PublicStayAvailability(true, null, house.MinNights, null),
            new PublicStayPrice(house.Currency, FromPerNightCents(house, data), null, null));

    private static long FromPerNightCents(PublicStayHouse house, PublicStayData data)
    {
        var hostPrices = HostPricesOf(data, house.Id);
        var lowest = decimal.MaxValue;
        for (var offset = 0; offset < PublicSiteSearch.FromPriceDays; offset++)
        {
            var price = StayPricing.NightPrice(
                data.Today.AddDays(offset), house.NightlyRate, house.WeekendSurchargePercent, hostPrices);
            lowest = Math.Min(lowest, price);
        }

        return StayPricing.ToCents(lowest);
    }

    private static PublicStayCard Card(
        PublicStayHouse house,
        PublicStayData data,
        PublicStayAvailability availability,
        PublicStayPrice price,
        StayCancellation? cancellation = null) =>
        new(
            house.Id,
            house.Slug,
            house.Name,
            house.City,
            house.Zone,
            house.Latitude,
            house.Longitude,
            house.Photos,
            house.Bedrooms,
            house.Bathrooms,
            house.MaxGuests,
            house.AreaSqm,
            MainAmenitiesOf(house),
            house.Tags,
            data.Ratings.TryGetValue(house.Id, out var rating) ? rating : null,
            availability,
            price,
            cancellation,
            house.OrgSlug,
            house.HostName,
            house.Country);

    private static IReadOnlyList<string> MainAmenitiesOf(PublicStayHouse house) =>
        MainAmenities.Where(house.Amenities.Contains).Take(MaxCardAmenities).Select(amenity => amenity.ToString()).ToList();

    private static IReadOnlyList<PublicStayCard> Order(IReadOnlyList<PublicStayCard> cards, string sort, bool hasDates, bool platform = false)
    {
        var free = cards.Where(card => card.Availability.Available);
        var others = cards.Where(card => !card.Availability.Available).OrderBy(card => card.Name, ByName).ToList();

        long PriceOf(PublicStayCard card) => hasDates ? card.Price.TotalCents ?? long.MaxValue : card.Price.PerNightCents;

        IOrderedEnumerable<PublicStayCard> ByRating() => free.OrderByDescending(card => card.Rating?.Average ?? -1m)
            .ThenByDescending(card => card.Rating?.Count ?? 0)
            .ThenBy(card => card.Name, ByName);

        IEnumerable<PublicStayCard> sorted = sort switch
        {
            PublicSiteSearch.PriceAsc => free.OrderBy(PriceOf).ThenBy(card => card.Name, ByName),
            PublicSiteSearch.PriceDesc => free.OrderByDescending(card => card.Price.TotalCents is null && hasDates ? long.MinValue : PriceOf(card))
                .ThenBy(card => card.Name, ByName),
            PublicSiteSearch.Rating => ByRating(),
            PublicSiteSearch.Largest => free.OrderByDescending(card => card.AreaSqm ?? -1).ThenBy(card => card.Name, ByName),
            _ => platform ? ByRating() : free.OrderBy(card => card.Name, ByName),
        };

        return [.. sorted, .. others];
    }

    private static readonly IComparer<string> ByName = Comparer<string>.Create(
        (a, b) => Italian.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace));

    private static List<PublicStayAlternative> Alternatives(
        IReadOnlyList<PublicStayHouse> pool,
        PublicStayData data,
        PublicStaysCriteria criteria,
        DateOnly checkIn,
        int nights)
    {
        var party = criteria.Adults + criteria.Children;

        var candidates = !data.AcceptsBookings
            ? []
            : pool.Where(house => party <= house.MaxGuests && (!criteria.Pets || house.AcceptsPets)).ToList();

        var todays = candidates.ToDictionary(house => house.Id, house => TodayOf(data, house));
        var found = new List<PublicStayAlternative>();
        foreach (var offset in AlternativeOffsets)
        {
            var start = checkIn.AddDays(offset);
            if (start < data.Today)
                continue;

            var end = start.AddDays(nights);
            var count = 0;
            var lowest = long.MaxValue;
            foreach (var house in candidates)
            {
                if (AnyTaken(TakenOf(data, house.Id), start, end)
                    || DayRuleStayChecks.FirstViolation(start, end, todays[house.Id], house.MinNights, RulesOf(data, house.Id)) is not null)
                {
                    continue;
                }

                count++;
                var lodging = StayPricing.Lodging(start, nights, house.NightlyRate, house.WeekendSurchargePercent, HostPricesOf(data, house.Id));
                lowest = Math.Min(lowest, StayPricing.ToCents(lodging.Total / nights));
            }

            if (count > 0)
                found.Add(new PublicStayAlternative(start, end, count, lowest));

            if (found.Count == PublicSiteSearch.MaxAlternatives)
                break;
        }

        return [.. found.OrderBy(alternative => alternative.CheckIn)];
    }

    public static IReadOnlyList<PublicSiteCalendarDay> SiteDays(
        IReadOnlyList<PublicStayHouse> houses,
        PublicStayData data,
        DateOnly first,
        DateOnly to,
        int party)
    {
        ArgumentNullException.ThrowIfNull(houses);
        ArgumentNullException.ThrowIfNull(data);

        var candidates = data.AcceptsBookings ? houses.Where(house => party <= house.MaxGuests).ToList() : [];
        var todays = candidates.ToDictionary(house => house.Id, house => TodayOf(data, house));
        var days = new List<PublicSiteCalendarDay>();
        for (var day = first; day < to; day = day.AddDays(1))
        {
            long? lowest = null;
            int? minNights = null;
            foreach (var house in candidates)
            {
                RulesOf(data, house.Id).TryGetValue(day, out var rule);
                if (TakenOf(data, house.Id).Contains(day) || !DayRuleStayChecks.CanArriveOn(day, todays[house.Id], rule))
                    continue;

                var price = StayPricing.ToCents(StayPricing.NightPrice(
                    day, house.NightlyRate, house.WeekendSurchargePercent, HostPricesOf(data, house.Id)));
                var minimum = DayRuleDefaults.MinNightsOf(rule, house.MinNights);
                lowest = lowest is { } current ? Math.Min(current, price) : price;
                minNights = minNights is { } shortest ? Math.Min(shortest, minimum) : minimum;
            }

            days.Add(new PublicSiteCalendarDay(day, lowest is not null, lowest, minNights));
        }

        return days;
    }

    public static IReadOnlyList<PublicDestination> Destinations(IReadOnlyList<PublicStayHouse> houses)
    {
        ArgumentNullException.ThrowIfNull(houses);

        return DestinationsOfCities(houses.Select(house => house.City));
    }

    public static IReadOnlyList<PublicDestination> DestinationsOfCities(IEnumerable<string> cities)
    {
        ArgumentNullException.ThrowIfNull(cities);

        return cities
            .Where(city => DestinationId(city).Length > 0)
            .GroupBy(DestinationId)
            .Select(group =>
            {
                var comune = group.GroupBy(city => city.Trim()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
                return new PublicDestination(group.Key, comune, comune, group.Count());
            })
            .OrderByDescending(destination => destination.Count)
            .ThenBy(destination => destination.Label, ByName)
            .ToList();
    }

    public static IReadOnlyList<PublicStayCard> Similar(
        PublicStayHouse target,
        IReadOnlyList<PublicStayHouse> houses,
        PublicStayData data,
        int max)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(houses);

        var targetComune = DestinationId(target.City);
        return houses
            .Where(house => house.Id != target.Id)
            .Select(house => (House: house, Score: (house.Tags.Intersect(target.Tags).Count() * 10m)
                - Math.Abs(house.NightlyRate - target.NightlyRate) / 20m
                + (DestinationId(house.City) == targetComune ? 6m : 0m)))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.House.Name, ByName)
            .Take(Math.Clamp(max, 1, PublicSiteSearch.MaxSimilar))
            .Select(entry => CardWithoutDates(entry.House, data))
            .ToList();
    }

    private static IReadOnlySet<DateOnly> TakenOf(PublicStayData data, Guid houseId) =>
        data.Taken.TryGetValue(houseId, out var taken) ? taken : NoNights;

    private static IReadOnlyDictionary<DateOnly, decimal>? HostPricesOf(PublicStayData data, Guid houseId) =>
        data.HostPrices.TryGetValue(houseId, out var prices) ? prices : null;

    private static DateOnly TodayOf(PublicStayData data, PublicStayHouse house) =>
        data.NowUtc is { } now
            ? DayRuleRules.TodayOf(house.Timezone, new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)))
            : data.Today;

    private static IReadOnlyDictionary<DateOnly, DayRuleValues> RulesOf(PublicStayData data, Guid houseId) =>
        data.DayRules is not null && data.DayRules.TryGetValue(houseId, out var rules) ? rules : DayRuleStayChecks.NoRules;

    private static bool AnyTaken(IReadOnlySet<DateOnly> taken, DateOnly from, DateOnly to)
    {
        if (taken.Count == 0)
            return false;

        for (var night = from; night < to; night = night.AddDays(1))
        {
            if (taken.Contains(night))
                return true;
        }

        return false;
    }
}
