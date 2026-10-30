using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
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
    IReadOnlyList<PropertyAmenity> Amenities)
{
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
    private const string Currency = "EUR";

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

        var unsupported = new List<string>();
        if (criteria.Tags.Count > 0)
            unsupported.Add("tags");
        var rules = new List<string>();
        foreach (var rule in criteria.Rules)
        {
            // The house rule about children is a fact the hosts cannot give yet (DB-11a): asked and not applied, said so.
            if (rule == "children")
                unsupported.Add("rules:children");
            else
                rules.Add(rule);
        }

        unsupported.AddRange(criteria.IgnoredValues);

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

        var ordered = Order(cards, criteria.Sort, hasDates);
        var availableCount = cards.Count(card => card.Availability.Available);
        var pageSize = Math.Clamp(criteria.PageSize ?? PublicSiteSearch.MaxPageSize, 1, PublicSiteSearch.MaxPageSize);
        var page = Math.Max(criteria.Page, 1);
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // Another date helps only the houses that pass every filter, the price too.
        var kept = cards.Select(card => card.Id).ToHashSet();
        var alternatives = hasDates && availableCount == 0
            ? Alternatives(pool.Where(house => kept.Contains(house.Id)).ToList(), data, criteria, checkIn, nights)
            : [];

        return new PublicStaysResult(
            Currency,
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
            generatedAt);
    }

    private static bool PassesFilters(
        PublicStayHouse house,
        PublicStaysCriteria criteria,
        IReadOnlyList<PropertyAmenity> amenities,
        IReadOnlyList<string> rules)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Where) && !MatchesPlace(house, criteria.Where))
            return false;
        if (criteria.Bedrooms is { } bedrooms && house.Bedrooms < bedrooms)
            return false;
        if (criteria.Bathrooms is { } bathrooms && house.Bathrooms < bathrooms)
            return false;
        if (criteria.MinSqm is { } sqm && (house.AreaSqm is not { } area || area < sqm))
            return false;
        if (!amenities.All(house.Amenities.Contains))
            return false;

        return rules.All(rule => rule switch
        {
            "pets" => house.AcceptsPets,
            "selfCheckin" => house.HasSelfCheckIn,
            "smoking" => house.AllowsSmoking,
            "parties" => house.AllowsParties,
            _ => true,
        });
    }

    private static bool MatchesPlace(PublicStayHouse house, string place)
    {
        var wanted = TouristTaxComune.NormalizeName(place).Replace(' ', '-');
        return DestinationId(house.City) == wanted;
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

        return Card(
            house,
            data,
            new PublicStayAvailability(reason is null, reason, house.MinNights, firstFree),
            new PublicStayPrice(Currency, StayPricing.ToCents(lodging.Total / nights), total, includes));
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
            new PublicStayPrice(Currency, FromPerNightCents(house, data), null, null));

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
        PublicStayPrice price) =>
        new(
            house.Id,
            house.Slug,
            house.Name,
            house.City,
            Zone: null,
            house.Latitude,
            house.Longitude,
            house.Photos,
            house.Bedrooms,
            house.Bathrooms,
            house.MaxGuests,
            house.AreaSqm,
            MainAmenitiesOf(house),
            Tags: [],
            data.Ratings.TryGetValue(house.Id, out var rating) ? rating : null,
            availability,
            price);

    private static IReadOnlyList<string> MainAmenitiesOf(PublicStayHouse house) =>
        MainAmenities.Where(house.Amenities.Contains).Take(MaxCardAmenities).Select(amenity => amenity.ToString()).ToList();

    // ─── Order ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The free houses in the sort asked, then the others by name, as the design shows them.</summary>
    private static IReadOnlyList<PublicStayCard> Order(IReadOnlyList<PublicStayCard> cards, string sort, bool hasDates)
    {
        var free = cards.Where(card => card.Availability.Available);
        var others = cards.Where(card => !card.Availability.Available).OrderBy(card => card.Name, ByName).ToList();

        long PriceOf(PublicStayCard card) => hasDates ? card.Price.TotalCents ?? long.MaxValue : card.Price.PerNightCents;

        IEnumerable<PublicStayCard> sorted = sort switch
        {
            PublicSiteSearch.PriceAsc => free.OrderBy(PriceOf).ThenBy(card => card.Name, ByName),
            PublicSiteSearch.PriceDesc => free.OrderByDescending(card => card.Price.TotalCents is null && hasDates ? long.MinValue : PriceOf(card))
                .ThenBy(card => card.Name, ByName),
            PublicSiteSearch.Rating => free.OrderByDescending(card => card.Rating?.Average ?? -1m)
                .ThenByDescending(card => card.Rating?.Count ?? 0)
                .ThenBy(card => card.Name, ByName),
            PublicSiteSearch.Largest => free.OrderByDescending(card => card.AreaSqm ?? -1).ThenBy(card => card.Name, ByName),
            _ => free.OrderBy(card => card.Name, ByName),
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

        return houses
            .Where(house => DestinationId(house.City).Length > 0)
            .GroupBy(house => DestinationId(house.City))
            .Select(group =>
            {
                // The way the hosts wrote the comune most often (they may differ by an accent or a capital).
                var comune = group.GroupBy(house => house.City.Trim()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
                return new PublicDestination(group.Key, comune, comune, group.Count());
            })
            .OrderByDescending(destination => destination.Count)
            .ThenBy(destination => destination.Label, ByName)
            .ToList();
    }

    /// <summary>
    /// Up to <paramref name="max"/> other houses that look like <paramref name="target"/> (<c>similarTo</c> of the design): the same kind of
    /// place (the amenities that make a house a «mare», «con piscina»…), the same comune and a price near. The cards carry the «from» price,
    /// no stay.
    /// </summary>
    public static IReadOnlyList<PublicStayCard> Similar(
        PublicStayHouse target,
        IReadOnlyList<PublicStayHouse> houses,
        PublicStayData data,
        int max)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(houses);

        var targetKinds = KindsOf(target);
        var targetComune = DestinationId(target.City);
        return houses
            .Where(house => house.Id != target.Id)
            .Select(house => (House: house, Score: (KindsOf(house).Intersect(targetKinds).Count() * 10m)
                - Math.Abs(house.NightlyRate - target.NightlyRate) / 20m
                + (DestinationId(house.City) == targetComune ? 6m : 0m)))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.House.Name, ByName)
            .Take(Math.Clamp(max, 1, PublicSiteSearch.MaxSimilar))
            .Select(entry => CardWithoutDates(entry.House, data))
            .ToList();
    }

    // What makes a house a kind of place, from the amenities (the labels of DB-11a will say it better).
    private static IEnumerable<PropertyAmenity> KindsOf(PublicStayHouse house) =>
        house.Amenities.Where(amenity => amenity is PropertyAmenity.Pool or PropertyAmenity.BeachAccess or PropertyAmenity.Terrace
            or PropertyAmenity.Garden or PropertyAmenity.PetFriendly);

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
