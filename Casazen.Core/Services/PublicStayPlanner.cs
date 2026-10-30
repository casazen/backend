using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.SiteProfile;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

/// <summary>A published house of the site with what the search needs of it: a projection, nothing private.</summary>
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
    string Country = PublicStayHouse.DefaultCountry)
{
    /// <summary>What a house without a currency of its own costs in (the property says <c>EUR</c> until it is in Switzerland).</summary>
    public const string DefaultCurrency = "EUR";

    public const string DefaultCountry = "IT";

    /// <summary>The labels of the house (what the host chose and what its data give), in the order of the vocabulary.</summary>
    public IReadOnlyList<string> Tags => Labels ?? [];

    /// <summary>Pets are welcome, or welcome on request (the host decides then): a stay with a pet is not refused for it. Without the rule, the amenity says.</summary>
    public bool AcceptsPets => Pets is { } rule ? rule != PetsPolicy.No : Amenities.Contains(PropertyAmenity.PetFriendly);

    public bool AllowsSmoking => Smoking is { } rule ? rule == SmokingPolicy.Yes : Amenities.Contains(PropertyAmenity.SmokingAllowed);

    /// <summary>The rule has no «yes», only «on request»: a house where parties are possible at all.</summary>
    public bool AllowsParties => Parties == PartiesPolicy.OnRequest;

    public bool HasSelfCheckIn => Amenities.Contains(PropertyAmenity.SelfCheckIn);
}

/// <summary>
/// What the search reads about the houses of one site, loaded for all of them together: the taken nights, the prices the hosts confirmed,
/// the tourist tax rates of the period and the ratings.
/// </summary>
/// <param name="Today">Today in Europe/Rome.</param>
/// <param name="AcceptsBookings">The host can take a booking on the site (<see cref="Org.CanTakeDirectPayments"/>).</param>
/// <param name="Taken">The taken nights of each house (<see cref="PublicOccupancyReader"/> in the Infrastructure).</param>
/// <param name="HostPrices">The prices the host confirmed for dates, of each house (<see cref="IPricingAdapterService.GetAppliedNightlyPricesAsync"/> for many).</param>
/// <param name="TaxRates">The active tourist tax rates of every comune that overlap the stay: the planner picks the ones of each house.</param>
/// <param name="Ratings">The public rating of each house that has reviews.</param>
public sealed record PublicStayData(
    DateOnly Today,
    bool AcceptsBookings,
    IReadOnlyDictionary<Guid, IReadOnlySet<DateOnly>> Taken,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>> HostPrices,
    IReadOnlyList<TouristTaxRate> TaxRates,
    IReadOnlyDictionary<Guid, PublicRatingSummary> Ratings);

/// <summary>
/// The decisions of the search of the site (DB-07a), as pure functions of the houses and of what was read about them: which house is free
/// and why not, what the stay costs, the first free date, the nearby dates, the days of the whole site, the places and the houses that look
/// alike. The price of a night and of a stay is the quote's own (<see cref="StayPricing"/>, <see cref="TouristTaxCalculator"/>); the planner
/// has no price rule of its own, so the list, the calendar and the quote cannot disagree.
/// </summary>
public static class PublicStayPlanner
{
    /// <summary>
    /// The currency a list or a calendar says it is in: the one of its houses when they share it, else euros (the houses of two countries
    /// in one list each carry their own on their price; amounts of two currencies are not converted).
    /// </summary>
    public static string CurrencyOf(IEnumerable<PublicStayHouse> houses)
    {
        var currencies = houses.Select(house => house.Currency).Distinct(StringComparer.Ordinal).Take(2).ToList();
        return currencies.Count == 1 ? currencies[0] : PublicStayHouse.DefaultCurrency;
    }

    /// <summary>The age every child is counted at while the list does not know theirs: the oldest minor, a payer wherever a minor pays.</summary>
    private const int EstimatedChildAge = TouristTaxCalculator.AdultAge - 1;

    // The date offsets tried when nothing is free (altDates of the design): the nearest first, a day later before a day earlier.
    private static readonly int[] AlternativeOffsets = [1, -1, 2, -2, 3, -3, 4, 5, 6, 7, 10, 14, 21];

    // The amenities a card shows, in this order, at most six.
    private static readonly PropertyAmenity[] MainAmenities =
    [
        PropertyAmenity.Pool, PropertyAmenity.BeachAccess, PropertyAmenity.Terrace, PropertyAmenity.Garden, PropertyAmenity.Balcony,
        PropertyAmenity.AirConditioning, PropertyAmenity.WiFi, PropertyAmenity.FreeParking, PropertyAmenity.PetFriendly,
        PropertyAmenity.Washer, PropertyAmenity.Kitchen,
    ];

    private const int MaxCardAmenities = 6;

    private static readonly IReadOnlySet<DateOnly> NoNights = new HashSet<DateOnly>();

    private static readonly CompareInfo Italian = CultureInfo.GetCultureInfo("it-IT").CompareInfo;

    /// <summary>The id of a comune as a place of the site: its normalized name with hyphens (<c>polignano-a-mare</c>).</summary>
    public static string DestinationId(string? city) => TouristTaxComune.NormalizeName(city).Replace(' ', '-');

    // ─── The list ───────────────────────────────────────────────────────────────────────────────────

    public static PublicStaysResult Search(
        IReadOnlyList<PublicStayHouse> houses,
        PublicStayData data,
        PublicStaysCriteria criteria,
        DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(houses);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(criteria);

        // What no house can match (an amenity, a rule or a label that does not exist) was left out by the query and is declared here.
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

        // The search across all the sites lists what can be booked (a long list would be mostly houses taken or too small for the party),
        // best rated first, a page at a time; the houses that are not free are counted in TotalCount and not listed.
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

        // Another date helps only the houses that pass every filter, the price too.
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

    /// <summary>
    /// The houses in the place asked (all of them for an empty place). On a site the place is the id of a destination, or the name of a
    /// comune; across all the sites it is what the visitor typed, so the comune or the zone the host wrote only has to contain it, and a
    /// phrase that contains the name of the comune finds it too («lago di como»).
    /// </summary>
    public static IReadOnlyList<PublicStayHouse> InPlace(IReadOnlyList<PublicStayHouse> houses, string? place, bool lenient)
    {
        ArgumentNullException.ThrowIfNull(houses);

        return string.IsNullOrWhiteSpace(place) ? houses : houses.Where(house => MatchesPlace(house, place, lenient)).ToList();
    }

    /// <summary>What the controls of the search need to know about <paramref name="houses"/>: the reach of the sliders, the guests, the pets, the labels.</summary>
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
            // Children are welcome only when the host said so (DB-11a): a house that never said is not offered for «adatta ai bambini».
            "children" => house.ChildrenWelcome == true,
            _ => true,
        });
    }

    // A comune this short is a word of other names («Lu», «Ro»): a phrase that contains it does not find it.
    private const int MinCityLengthForPhrase = 4;

    // The countries CasaZen has houses in (IT and CH, ADR-001-R7), by the ids of what a visitor types for them (see DestinationId).
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

        // The country, by the names people use for it: «svizzera» or «switzerland» find the houses in Switzerland.
        if (CountryNames.TryGetValue(wanted, out var country))
            return string.Equals(house.Country, country, StringComparison.OrdinalIgnoreCase);

        return city.Contains(wanted, StringComparison.Ordinal)
            || (city.Length >= MinCityLengthForPhrase && wanted.Contains(city, StringComparison.Ordinal))
            || ZoneIds(house).Any(zone => zone.Contains(wanted, StringComparison.Ordinal));
    }

    /// <summary>The zone the host wrote, in both languages, as ids comparable with a typed place.</summary>
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

    // ─── A house for the dates asked ────────────────────────────────────────────────────────────────

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
        var party = criteria.Adults + criteria.Children;

        var reason = ReasonNotFree(house, data, criteria, party, checkIn, checkOut, nights, taken);
        DateOnly? firstFree = reason switch
        {
            null => checkIn,
            PublicStayReasons.Busy or PublicStayReasons.MinNights => NextFreeCheckIn(
                taken, checkIn < data.Today ? data.Today : checkIn, nights, house.MinNights),
            _ => null,
        };

        var lodging = StayPricing.Lodging(checkIn, nights, house.NightlyRate, house.WeekendSurchargePercent, hostPrices);
        long? total = null;
        string? includes = null;

        // A total only for a stay the quote of the checkout would price: in the future, not longer than the longest priced, for a party
        // the house sleeps, not shorter than its minimum stay. Busy or not, the price of the stay is still the price of the stay.
        var quotable = checkIn >= data.Today
            && nights <= TouristTaxCalculator.MaxNights
            && party <= house.MaxGuests
            && (house.MinNights is not { } minimum || nights >= minimum);
        if (quotable)
        {
            var tax = TouristTaxOf(house, data, rateCache, checkIn, checkOut, criteria, lodging);
            total = StayPricing.ToCents(lodging.Total + house.CleaningFee + tax.AmountOrZero);
            includes = tax.Status != TouristTaxQuoteStatus.Calculated
                ? PublicStayTaxStates.NotIncluded
                : criteria.Children > 0 && tax.AgeRulesApply ? PublicStayTaxStates.Estimated : PublicStayTaxStates.Exact;
        }

        // What cancelling costs for this stay, on the policy of the house with what the host changed of it: the dates of the quote.
        var cancellation = house.CatalogPolicy is null
            ? null
            : PublicCancellation.StayOf(
                CancellationRefundPolicy.EffectivePolicy(house.CatalogPolicy, house.Overrides, nights),
                checkIn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                data.Today);

        return Card(
            house,
            data,
            new PublicStayAvailability(reason is null, reason, house.MinNights, firstFree),
            new PublicStayPrice(house.Currency, StayPricing.ToCents(lodging.Total / nights), total, includes),
            cancellation);
    }

    /// <summary>Why the house is not free for the stay, in the order of the design (<c>stayEval</c>); <c>null</c> when it is.</summary>
    private static string? ReasonNotFree(
        PublicStayHouse house,
        PublicStayData data,
        PublicStaysCriteria criteria,
        int party,
        DateOnly checkIn,
        DateOnly checkOut,
        int nights,
        IReadOnlySet<DateOnly> taken)
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
        if (house.MinNights is { } minimum && nights < minimum)
            return PublicStayReasons.MinNights;

        return null;
    }

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

        // The ages of the children are not known to a list: every child is counted at the oldest age a minor has, so a rate that charges
        // some minors charges them (an upper bound, said so with «estimated»); a rate that does not look at ages gives the exact figure.
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
    /// The first day, from <paramref name="after"/> on, a stay as long as the one asked (and at least the minimum stay) can start in the
    /// next <see cref="PublicSiteSearch.FirstFreeHorizonDays"/> days, or <c>null</c> (<c>nextFree</c> of the design).
    /// </summary>
    private static DateOnly? NextFreeCheckIn(IReadOnlySet<DateOnly> taken, DateOnly after, int nights, int? minNights)
    {
        var length = Math.Max(nights, minNights ?? 1);
        for (var offset = 0; offset < PublicSiteSearch.FirstFreeHorizonDays; offset++)
        {
            var start = after.AddDays(offset);
            if (!AnyTaken(taken, start, start.AddDays(length)))
                return start;
        }

        return null;
    }

    // ─── A house without dates ──────────────────────────────────────────────────────────────────────

    private static PublicStayCard CardWithoutDates(PublicStayHouse house, PublicStayData data) =>
        Card(
            house,
            data,
            new PublicStayAvailability(true, null, house.MinNights, null),
            new PublicStayPrice(house.Currency, FromPerNightCents(house, data), null, null));

    /// <summary>The lowest night of the next <see cref="PublicSiteSearch.FromPriceDays"/> days: the «da X € a notte» of a house.</summary>
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

    // ─── Order ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The free houses in the sort asked, then the others by name, as the design shows them.</summary>
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
            // Across all the sites «recommended» is the best rated first (houses of many hosts have no order of their own); on a site, by name.
            _ => platform ? ByRating() : free.OrderBy(card => card.Name, ByName),
        };

        return [.. sorted, .. others];
    }

    private static readonly IComparer<string> ByName = Comparer<string>.Create(
        (a, b) => Italian.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace));

    // ─── Nearby dates ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When no house is free: up to three ranges of the same length, a few days from the one asked, in which some house that could host the
    /// party is (<c>altDates</c> of the design), with how many and the lowest price per night; oldest first.
    /// </summary>
    private static List<PublicStayAlternative> Alternatives(
        IReadOnlyList<PublicStayHouse> pool,
        PublicStayData data,
        PublicStaysCriteria criteria,
        DateOnly checkIn,
        int nights)
    {
        var party = criteria.Adults + criteria.Children;

        // A house that cannot sleep the party, or take the pet, or be booked at all, is not helped by another date.
        var candidates = !data.AcceptsBookings
            ? []
            : pool.Where(house => party <= house.MaxGuests && (!criteria.Pets || house.AcceptsPets)).ToList();

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
                if ((house.MinNights is { } minimum && nights < minimum) || AnyTaken(TakenOf(data, house.Id), start, end))
                    continue;

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

    // ─── The days of the whole site ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// For each day from <paramref name="first"/> to <paramref name="to"/> (excluded): whether some house that sleeps the party is free for
    /// the night, the cheapest night among those houses and the shortest minimum stay (the "more houses" mode of the date picker of the home).
    /// </summary>
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
        var days = new List<PublicSiteCalendarDay>();
        for (var day = first; day < to; day = day.AddDays(1))
        {
            long? lowest = null;
            int? minNights = null;
            foreach (var house in candidates)
            {
                if (TakenOf(data, house.Id).Contains(day))
                    continue;

                var price = StayPricing.ToCents(StayPricing.NightPrice(
                    day, house.NightlyRate, house.WeekendSurchargePercent, HostPricesOf(data, house.Id)));
                lowest = lowest is { } current ? Math.Min(current, price) : price;
                minNights = minNights is { } shortest ? Math.Min(shortest, house.MinNights ?? 1) : house.MinNights ?? 1;
            }

            days.Add(new PublicSiteCalendarDay(day, lowest is not null, lowest, minNights));
        }

        return days;
    }

    // ─── The places and the houses that look alike ──────────────────────────────────────────────────

    /// <summary>The comuni of the houses, the most houses first.</summary>
    public static IReadOnlyList<PublicDestination> Destinations(IReadOnlyList<PublicStayHouse> houses)
    {
        ArgumentNullException.ThrowIfNull(houses);

        return DestinationsOfCities(houses.Select(house => house.City));
    }

    /// <summary>The same places from the written comuni of the houses alone (the platform lists them without loading the houses).</summary>
    public static IReadOnlyList<PublicDestination> DestinationsOfCities(IEnumerable<string> cities)
    {
        ArgumentNullException.ThrowIfNull(cities);

        return cities
            .Where(city => DestinationId(city).Length > 0)
            .GroupBy(DestinationId)
            .Select(group =>
            {
                // The way the hosts wrote the comune most often (they may differ by an accent or a capital).
                var comune = group.GroupBy(city => city.Trim()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
                return new PublicDestination(group.Key, comune, comune, group.Count());
            })
            .OrderByDescending(destination => destination.Count)
            .ThenBy(destination => destination.Label, ByName)
            .ToList();
    }

    /// <summary>
    /// Up to <paramref name="max"/> other houses that look like <paramref name="target"/> (<c>similarTo</c> of the design): the same kind of
    /// place (the labels they share: «mare», «con piscina»…), the same comune and a price near. The cards carry the «from» price, no stay.
    /// </summary>
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

    // ─── Small helpers ──────────────────────────────────────────────────────────────────────────────

    private static IReadOnlySet<DateOnly> TakenOf(PublicStayData data, Guid houseId) =>
        data.Taken.TryGetValue(houseId, out var taken) ? taken : NoNights;

    private static IReadOnlyDictionary<DateOnly, decimal>? HostPricesOf(PublicStayData data, Guid houseId) =>
        data.HostPrices.TryGetValue(houseId, out var prices) ? prices : null;

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
