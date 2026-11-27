using System.Globalization;
using Casazen.Core.Authorization;

namespace Casazen.Core.Search;

public static class SearchTypes
{
    public const string Property = "property";

    public const string Booking = "booking";

    public const string Guest = "guest";

    public const string Lease = "lease";

    public const string ServiceRequest = "service-request";

    public const string Supplier = "supplier";

    public const string SupplierRequest = "supplier-request";

    public static IReadOnlyList<string> All { get; } =
    [
        Property, Booking, Guest, Lease, ServiceRequest, Supplier, SupplierRequest,
    ];
}

public static class SearchDestinations
{
    public const string ShortRentProperty = "short-rent.property";
    public const string LongRentProperty = "long-rent.property";
    public const string ShortRentBooking = "short-rent.booking";
    public const string ShortRentGuest = "short-rent.guest";
    public const string LongRentLease = "long-rent.lease";
    public const string ShortRentServiceRequest = "short-rent.service-request";
    public const string LongRentServiceRequest = "long-rent.service-request";
    public const string ShortRentSupplier = "short-rent.supplier";
    public const string SupplierRequest = "supplier.request";
}

public static class SearchLimits
{
    public const int DefaultLimit = 5;

    public const int MaxLimit = 20;
}

public static class SearchErrorCodes
{
    public const string QueryTooShort = "search_query_too_short";

    public const string QueryTooShortMessageKey = "SearchQueryTooShort";

    public const string QueryTooLong = "search_query_too_long";

    public const string QueryTooLongMessageKey = "SearchQueryTooLong";
}

/// <summary>One result: what the palette shows and where it opens. Nothing sensitive: see <see cref="SearchTypes"/> for what each kind carries.</summary>
/// <param name="Type">The kind of result (<see cref="SearchTypes"/>).</param>
/// <param name="Id">The id of the object, for the route of the destination.</param>
/// <param name="Title">A short title: a name, a code, a service.</param>
/// <param name="Subtitle">A line that tells it apart from the others (the property, the city, a masked e-mail), or null.</param>
/// <param name="Destination">The destination key (<see cref="SearchDestinations"/>).</param>
public sealed record SearchHit(string Type, Guid Id, string Title, string? Subtitle, string Destination);

public sealed record SearchGroup(string Type, IReadOnlyList<SearchHit> Items, bool HasMore);

public sealed record GlobalSearchResult(IReadOnlyList<SearchGroup> Groups)
{
    public static GlobalSearchResult Empty { get; } = new([]);
}

/// <summary>
/// What the caller may search as a host: its scope on the properties of its org (<see cref="HostScope"/>, from
/// <see cref="IHostScopeResolver"/>, never widened) and, for each kind, the permission it holds in the context the kind belongs
/// to. Decided by the web layer with the same policies as the endpoints (<c>CasazenPolicies</c>); the search service only reads it.
/// </summary>
/// <param name="Scope">The org and the properties the caller reaches.</param>
/// <param name="ShortRentProperties">Holds <c>property.read</c> in the short-rent context: properties in short-rent mode, their service requests, the suppliers.</param>
/// <param name="LongRentProperties">Holds <c>property.read</c> in the long-rent context: properties in long-term mode and their service requests.</param>
/// <param name="Bookings">Holds <c>booking.read</c> (short-rent).</param>
/// <param name="Guests">Holds <c>guest.read</c> (short-rent).</param>
/// <param name="Leases">Holds <c>lease.read</c> (long-rent).</param>
public sealed record HostSearchAccess(
    HostScope Scope,
    bool ShortRentProperties,
    bool LongRentProperties,
    bool Bookings,
    bool Guests,
    bool Leases);

/// <summary>A search to run: the term, the most results per group and what the caller may search.</summary>
/// <param name="Query">The term, parsed (<see cref="SearchText.Parse"/>).</param>
/// <param name="Limit">Results per group, 1 to <see cref="SearchLimits.MaxLimit"/>.</param>
/// <param name="Host">What the caller may search of its org; null when it has no host permission or no reach.</param>
/// <param name="SupplierOrgId">The supplier org of the caller when it works as a supplier (its inbox is searched); null otherwise.</param>
/// <param name="Culture">The language of the labels the service writes (the name of a service category).</param>
public sealed record GlobalSearchRequest(
    SearchQuery Query,
    int Limit,
    HostSearchAccess? Host,
    Guid? SupplierOrgId,
    CultureInfo Culture);
