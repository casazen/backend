using Casazen.Core.SiteProfile;

namespace Casazen.Core.Services;

/// <summary>
/// The search of the public booking site of one host (DB-07a): for the dates and the guests chosen, every house of the site with whether it
/// is free (or why not), what the stay costs, when the first free date is and, when nothing is free, nearby dates that are; the calendar
/// of the whole site for the «Quando» field; the places for «Dove»; the houses that look like another. One definition of price (the
/// quote's own: <see cref="StayPricing"/> and <see cref="TouristTax.TouristTaxCalculator"/>) and one of availability
/// (<see cref="PublicOccupancyReader"/>), read for all the houses in a handful of queries, never one call per house.
/// </summary>
public interface IPublicSiteSearchService
{
    /// <summary>The houses of the site for <paramref name="criteria"/> (at most <see cref="PublicSiteSearch.MaxHouses"/>).</summary>
    Task<PublicStaysResult> SearchStaysAsync(Entities.Org org, PublicStaysCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>
    /// The days of the site from <paramref name="from"/> (default today) to <paramref name="to"/> (excluded, default 12 months later; 13 at
    /// most) for a party of <paramref name="adults"/> and <paramref name="children"/>.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="PublicSiteSearchErrorCodes.CalendarRangeInvalid"/>.</exception>
    Task<PublicSiteCalendar> GetSiteCalendarAsync(
        Entities.Org org,
        DateOnly? from,
        DateOnly? to,
        int adults,
        int children,
        CancellationToken cancellationToken = default);

    /// <summary>The comuni of the published houses of the site, for the «Dove» field.</summary>
    Task<IReadOnlyList<PublicDestination>> GetDestinationsAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to <paramref name="max"/> houses of the site that look like <paramref name="propertySlugOrId"/>, never the house itself;
    /// <c>null</c> when that is not a published house of the site.
    /// </summary>
    Task<IReadOnlyList<PublicStayCard>?> GetSimilarAsync(
        Guid orgId,
        string propertySlugOrId,
        int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same search across ALL the booking sites (the public <c>/search</c> of CasaZen): the published houses of the active orgs that can
    /// take a booking, with the same availability, total and cancellation of <see cref="SearchStaysAsync"/> (one definition of price), plus
    /// the site of each house (<see cref="PublicStayCard.OrgSlug"/>) and the facets of the place. Only the houses that are free for the stay
    /// are listed (the others are counted in <see cref="PublicStaysResult.TotalCount"/>); the list is paged.
    /// </summary>
    Task<PublicStaysResult> SearchPlatformStaysAsync(PublicStaysCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>
    /// The days of all the sites for the «Quando» field of the platform search, as <see cref="GetSiteCalendarAsync"/>; with
    /// <paramref name="where"/> only the houses of that place count.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="PublicSiteSearchErrorCodes.CalendarRangeInvalid"/>.</exception>
    Task<PublicSiteCalendar> GetPlatformCalendarAsync(
        string? where,
        DateOnly? from,
        DateOnly? to,
        int adults,
        int children,
        CancellationToken cancellationToken = default);

    /// <summary>The comuni of the houses of all the sites, the most houses first (at most <see cref="PublicSiteSearch.MaxPlatformDestinations"/>).</summary>
    Task<IReadOnlyList<PublicDestination>> GetPlatformDestinationsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Which houses a search looks at: the ones of one booking site, or the ones of every site (the platform search).</summary>
public enum PublicStaysScope
{
    /// <summary>The houses of one org (<c>/api/public/orgs/{slug}/stays</c>).</summary>
    Site = 0,

    /// <summary>The houses of every active org that can take a booking (<c>/api/public/stays</c>).</summary>
    Platform = 1,
}

/// <summary>Limits and constants of the search of the site.</summary>
public static class PublicSiteSearch
{
    /// <summary>Houses of a site the search reads: a site with more is cut at the first 50 by name.</summary>
    public const int MaxHouses = 50;

    /// <summary>
    /// Houses the platform search reads across all the sites (cut at the first ones by name, with a published house of an active org that
    /// can take a booking): past this a search that does not run in memory is needed (debt SEARCH-GLOBAL-FU3).
    /// </summary>
    public const int MaxPlatformHouses = 500;

    /// <summary>Houses of a page of the platform search when the caller does not say.</summary>
    public const int DefaultPlatformPageSize = 24;

    /// <summary>Places the platform search offers for «Dove».</summary>
    public const int MaxPlatformDestinations = 100;

    /// <summary>Largest page of the list.</summary>
    public const int MaxPageSize = 50;

    /// <summary>Days ahead the search looks for the first free date of a house that is taken (<c>nextFree</c> of the design).</summary>
    public const int FirstFreeHorizonDays = 150;

    /// <summary>Days ahead of today the «from» price of a house without dates is the lowest night of.</summary>
    public const int FromPriceDays = 30;

    /// <summary>Alternative date ranges offered when nothing is free.</summary>
    public const int MaxAlternatives = 3;

    /// <summary>Houses «similar» returns by default and at most.</summary>
    public const int DefaultSimilar = 3;

    public const int MaxSimilar = 6;

    /// <summary>The sorts of the list; anything else is read as <see cref="Recommended"/>.</summary>
    public const string Recommended = "recommended";

    public const string PriceAsc = "priceAsc";

    public const string PriceDesc = "priceDesc";

    public const string Rating = "rating";

    public const string Largest = "largest";
}

/// <summary>Why a house is not free for the stay asked: the stable <c>reason</c> of <see cref="PublicStayAvailability"/>.</summary>
public static class PublicStayReasons
{
    /// <summary>A night of the stay is taken (booking, hold, request, iCal or manual block: the answer does not say which).</summary>
    public const string Busy = "busy";

    /// <summary>The stay is shorter than the minimum stay of the house.</summary>
    public const string MinNights = "min_nights";

    /// <summary>The house sleeps fewer people than the party.</summary>
    public const string Guests = "guests";

    /// <summary>The party has a pet and the house does not accept pets.</summary>
    public const string Pets = "pets";

    /// <summary>The check-in day is before today.</summary>
    public const string Past = "past";

    /// <summary>The host cannot take a booking on the site yet (no payment account ready).</summary>
    public const string NotBookable = "not_bookable";
}

/// <summary>How much of the tourist tax a price includes: the stable <c>includesTouristTax</c> of <see cref="PublicStayPrice"/>.</summary>
public static class PublicStayTaxStates
{
    /// <summary>The tax of the comune, worked out for exactly this party.</summary>
    public const string Exact = "exact";

    /// <summary>The tax of the comune with every child counted as a payer, because the list does not know their ages: the quote of the checkout gives the exact one.</summary>
    public const string Estimated = "estimated";

    /// <summary>CasaZen has no rate for the comune (or for some night): the total does not include the tax.</summary>
    public const string NotIncluded = "notIncluded";
}

/// <summary>Stable <c>code</c> values of the search of the site: never rename one.</summary>
public static class PublicSiteSearchErrorCodes
{
    /// <summary>422: the dates of the stay are not both given, are not in order, or the stay is longer than the longest one priced.</summary>
    public const string StaysDatesInvalid = "public_stays_dates_invalid";

    /// <summary>422: the end of the range of the calendar is not after its start, or the range is longer than 13 months.</summary>
    public const string CalendarRangeInvalid = "public_site_calendar_range_invalid";
}

/// <summary>What the visitor asked of the list (already read from the query, with the values that were not understood left out).</summary>
/// <param name="CheckIn">First night of the stay; <c>null</c> together with <paramref name="CheckOut"/> when no dates were chosen.</param>
/// <param name="CheckOut">Day of departure (excluded from the nights).</param>
/// <param name="Where">The place chosen: the id of a destination (<see cref="PublicDestination.Id"/>) or the name of a comune.</param>
/// <param name="MinPrice">Lowest price per night, in euros.</param>
/// <param name="MaxPrice">Highest price per night, in euros.</param>
/// <param name="Bedrooms">At least this many bedrooms.</param>
/// <param name="Bathrooms">At least this many bathrooms.</param>
/// <param name="MinSqm">At least this many square metres (a house that did not say its size is not shown).</param>
/// <param name="Amenities">Names of <see cref="Enums.PropertyAmenity"/>: the house has all of them.</param>
/// <param name="Rules"><c>pets</c>, <c>children</c>, <c>selfCheckin</c>, <c>smoking</c>, <c>parties</c>: the house allows all of them.</param>
/// <param name="Tags">Labels of the houses (<see cref="SitePropertyProfileLimits.Tags"/>): the house has all of them, the ones the host chose and the ones its data give.</param>
/// <param name="Sort">One of <see cref="PublicSiteSearch"/> (<c>recommended</c> by default).</param>
/// <param name="Page">First page is 1.</param>
/// <param name="PageSize"><c>null</c> for every house (at most <see cref="PublicSiteSearch.MaxPageSize"/>).</param>
/// <param name="IgnoredValues">Values of the filters that no house can match (an unknown amenity, rule or label), declared and not applied.</param>
/// <param name="Scope">
/// <see cref="PublicStaysScope.Platform"/> for the search across all the sites: the place is matched by what the visitor types (the comune or
/// the zone the host wrote contains it), the <c>recommended</c> sort puts the best rated first and the houses that are not free are not listed.
/// </param>
public sealed record PublicStaysCriteria(
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    int Adults,
    int Children,
    bool Pets,
    string? Where,
    decimal? MinPrice,
    decimal? MaxPrice,
    int? Bedrooms,
    int? Bathrooms,
    int? MinSqm,
    IReadOnlyList<string> Amenities,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Tags,
    string Sort,
    int Page,
    int? PageSize,
    IReadOnlyList<string> IgnoredValues,
    PublicStaysScope Scope = PublicStaysScope.Site);

/// <summary>The list: the houses, the nearby dates when nothing is free, and what could not be applied.</summary>
/// <param name="CheckIn">The stay asked, <c>null</c> without dates.</param>
/// <param name="Nights">Length of the stay, <c>null</c> without dates.</param>
/// <param name="TotalCount">Houses that pass the filters (before the page).</param>
/// <param name="AvailableCount">Of those, the ones that are free for the stay (all of them when no dates were chosen).</param>
/// <param name="Items">The page of houses: the free ones first, in the sort asked, then the others by name.</param>
/// <param name="Alternatives">Up to three nearby ranges of the same length with free houses; empty when some house is free already.</param>
/// <param name="UnsupportedFilters">Filters that were asked and not applied: a value no house can match (<c>amenities:…</c>, <c>rules:…</c> or <c>tags:…</c> of an unknown name).</param>
/// <param name="Facets">Only on the platform search: what the houses of the place asked allow the controls to offer; <c>null</c> on a site.</param>
public sealed record PublicStaysResult(
    string Currency,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    int? Nights,
    int Adults,
    int Children,
    bool Pets,
    int TotalCount,
    int AvailableCount,
    int Page,
    int PageSize,
    IReadOnlyList<PublicStayCard> Items,
    IReadOnlyList<PublicStayAlternative> Alternatives,
    IReadOnlyList<string> UnsupportedFilters,
    DateTime GeneratedAt,
    PublicStaysFacets? Facets = null);

/// <summary>
/// What the controls of the platform search need to know about the houses of the place asked (before the other filters): how far the
/// price and size sliders go, how many guests a house takes, whether any takes pets, and the labels with how many houses carry each.
/// </summary>
/// <param name="MinNightCents">The lowest base night rate among the houses; <c>null</c> without houses.</param>
/// <param name="MaxNightCents">The highest base night rate among the houses.</param>
/// <param name="MaxGuests">The most people any of the houses sleeps; 0 without houses.</param>
/// <param name="MinAreaSqm">The smallest size among the houses that said theirs.</param>
/// <param name="MaxAreaSqm">The largest size among the houses that said theirs.</param>
/// <param name="PetsAllowed">Some house accepts pets (welcome or on request).</param>
/// <param name="Tags">The labels of the houses with their counts, in the order of the vocabulary.</param>
public sealed record PublicStaysFacets(
    long? MinNightCents,
    long? MaxNightCents,
    int MaxGuests,
    int? MinAreaSqm,
    int? MaxAreaSqm,
    bool PetsAllowed,
    IReadOnlyList<PublicStayFacetTag> Tags);

/// <summary>A label of the houses and how many houses carry it.</summary>
public sealed record PublicStayFacetTag(string Tag, int Count);

/// <summary>A house of the site as the list shows it. Nothing private: the position is rounded, there is no address.</summary>
/// <param name="Zone">The zone the host wrote (DB-11a), in Italian and, if written, English; <c>null</c> = not said.</param>
/// <param name="Latitude">Rounded to about a kilometre (<see cref="Entities.PropertyAddress.ToPublicCoordinate"/>).</param>
/// <param name="Photos">The first photos, the first is the cover.</param>
/// <param name="Amenities">The main ones, as the names of <see cref="Enums.PropertyAmenity"/>.</param>
/// <param name="Tags">Labels of the house: the ones the host chose and the ones its data give (a pool, pets welcome, the coast...), in the order of the vocabulary.</param>
/// <param name="Rating">The number and the average of the public reviews; <c>null</c> without any.</param>
/// <param name="Cancellation">
/// What cancelling costs for the stay asked, on the policy of the house, with the dates of the quote (last whole day, Europe/Rome); <c>null</c>
/// without dates and for a house with no cancellation policy.
/// </param>
/// <param name="OrgSlug">Only on the platform search: the slug of the booking site of the house (<c>/book/{orgSlug}/property/{slug}</c>); <c>null</c> on a site.</param>
/// <param name="HostName">Only on the platform search: how the host is named on the site (the name the host chose, else the name of the org).</param>
public sealed record PublicStayCard(
    Guid Id,
    string? Slug,
    string Name,
    string Comune,
    LocalizedText? Zone,
    decimal Latitude,
    decimal Longitude,
    IReadOnlyList<string> Photos,
    int Bedrooms,
    int Bathrooms,
    int MaxGuests,
    int? AreaSqm,
    IReadOnlyList<string> Amenities,
    IReadOnlyList<string> Tags,
    PublicRatingSummary? Rating,
    PublicStayAvailability Availability,
    PublicStayPrice Price,
    StayCancellation? Cancellation = null,
    string? OrgSlug = null,
    string? HostName = null);

/// <summary>Whether a house is free for the stay asked.</summary>
/// <param name="Available">Free for the stay (and bookable); <c>true</c> when no dates were chosen, since there is nothing to be taken.</param>
/// <param name="Reason">Why not, one of <see cref="PublicStayReasons"/>; <c>null</c> when free.</param>
/// <param name="MinNights">The minimum stay of the house, <c>null</c> for none.</param>
/// <param name="FirstFreeCheckIn">
/// The first check-in day, not before the one asked and not before today, for which a stay as long as the one asked (and at least the
/// minimum stay) is free within <see cref="PublicSiteSearch.FirstFreeHorizonDays"/> days; the check-in asked when the house is free;
/// <c>null</c> when the reason is not a matter of dates or nothing is free in that time.
/// </param>
public sealed record PublicStayAvailability(bool Available, string? Reason, int? MinNights, DateOnly? FirstFreeCheckIn);

/// <summary>What the stay costs, worked out by the functions of the quote.</summary>
/// <param name="PerNightCents">
/// With dates: the nights of the stay (the nightly rate, the weekend surcharge, the prices the host confirmed) divided by their number;
/// without: the lowest night of the next <see cref="PublicSiteSearch.FromPriceDays"/> days. Cleaning and tourist tax are not in it.
/// </param>
/// <param name="TotalCents">
/// The total of the quote of that stay (nights, cleaning, tourist tax when it can be calculated), to the cent; <c>null</c> without dates
/// and when the quote would refuse the stay (too many guests, shorter than the minimum, in the past).
/// </param>
/// <param name="IncludesTouristTax">One of <see cref="PublicStayTaxStates"/>; <c>null</c> when there is no total.</param>
public sealed record PublicStayPrice(string Currency, long PerNightCents, long? TotalCents, string? IncludesTouristTax);

/// <summary>A range of dates near the one asked, as long as the stay, with free houses.</summary>
/// <param name="AvailableCount">Houses that pass the filters and are free in those dates.</param>
/// <param name="FromPerNightCents">The lowest price per night among them.</param>
public sealed record PublicStayAlternative(DateOnly CheckIn, DateOnly CheckOut, int AvailableCount, long FromPerNightCents);

/// <summary>The days of the whole site: for the «Quando» field of the home, a day is free when some house is.</summary>
public sealed record PublicSiteCalendar(
    DateOnly From,
    DateOnly To,
    DateOnly Today,
    string Currency,
    int Adults,
    int Children,
    IReadOnlyList<PublicSiteCalendarDay> Days,
    DateTime GeneratedAt);

/// <summary>One night of the site.</summary>
/// <param name="AnyAvailable">A stay can start that night in some house that sleeps the party.</param>
/// <param name="FromPriceCents">The cheapest night among those houses; <c>null</c> when none is free.</param>
/// <param name="MinNights">The shortest minimum stay among them (1 for none); <c>null</c> when none is free.</param>
public sealed record PublicSiteCalendarDay(DateOnly Date, bool AnyAvailable, long? FromPriceCents, int? MinNights);

/// <summary>A place of the site: the comune of some published houses.</summary>
/// <param name="Id">Stable text of the comune (<c>polignano-a-mare</c>): the value of the <c>where</c> filter of the list.</param>
/// <param name="Label">What to show (the comune).</param>
/// <param name="Comune">The comune as the hosts wrote it.</param>
/// <param name="Count">Published houses there.</param>
public sealed record PublicDestination(string Id, string Label, string Comune, int Count);
