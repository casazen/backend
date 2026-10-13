using System.Globalization;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Search;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Search;

/// <summary>
/// The global search of the palette (UI-13a, <see cref="IGlobalSearchService"/>): one short query per group the caller may read, each
/// bound in SQL to the org and, for a caller who reaches only some properties, to those (<c>InScope</c>, the rule of AM-03), each
/// ordered and cut to the limit. The words of the term are matched with the stored search keys and their full-text indexes
/// (<see cref="SearchKeyModel"/>); nothing is read in memory to be filtered, and no group is read that the web layer did not allow.
/// </summary>
/// <remarks>
/// <para><b>What each group matches and shows.</b> Properties: name, city, CIN (title the name, subtitle the city). Bookings: the
/// booking code (a prefix of at least <see cref="SearchText.MinBookingCodeLength"/> characters) or the name of the guest (title the guest,
/// subtitle the code and the property). Guests: name and e-mail; the e-mail is only searched, it is shown <b>masked</b>, and the phone and
/// the document are neither searched nor shown. Leases: the name of a tenant (title the tenants, subtitle the property); fiscal code
/// and e-mail of the parties are never read. Service requests of the host: the name of the service as the supplier wrote it, or the
/// category (title the service or the category in the caller's language, subtitle the property). Suppliers: only the ones the org
/// already asked for something (<c>OrgTrustedSupplier</c>, the list of the preferred suppliers, does not exist yet), by business
/// name. Requests of the supplier: its own inbox, by service name, comune or the code the customer quotes (title the service,
/// subtitle the comune: what the supplier sees before it takes the request, decision D9).</para>
/// <para><b>Who is left out.</b> A guest erased or anonymized (<see cref="Guest.DataAnonymizedDate"/>), a guest deleted by the host, a
/// party of a lease anonymized (<see cref="Party.AnonymizedAt"/>), a property deleted or switched off, a supplier that is not active. A
/// guest and a booking of a caller who reaches only some properties are those of its properties (a guest with no stay there is not
/// found).</para>
/// <para><b>No term in the logs.</b> A search term is often a person's name: it is never logged, only the number of groups found.</para>
/// </remarks>
public sealed class GlobalSearchService(AppDbContext db, ILogger<GlobalSearchService> logger) : IGlobalSearchService
{
    /// <summary>Tenants shown in the title of a lease: the others are told as a number.</summary>
    private const int TenantNamesInTitle = 2;

    /// <summary>Category labels shown in the subtitle of a supplier.</summary>
    private const int CategoriesInSubtitle = 2;

    /// <summary>Between the parts of a subtitle: a space, the middle dot (U+00B7), a space.</summary>
    public const string SubtitleSeparator = " \u00B7 ";

    private readonly bool _fullText = db.Database.IsNpgsql();

    /// <inheritdoc />
    public async Task<GlobalSearchResult> SearchAsync(GlobalSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Query.IsEmpty)
            return GlobalSearchResult.Empty;

        var limit = Math.Clamp(request.Limit, 1, SearchLimits.MaxLimit);
        var groups = new List<SearchGroup>();

        if (request.Host is { } host)
        {
            Add(groups, await PropertiesAsync(request.Query, limit, host, cancellationToken));
            Add(groups, await BookingsAsync(request.Query, limit, host, cancellationToken));
            Add(groups, await GuestsAsync(request.Query, limit, host, cancellationToken));
            Add(groups, await LeasesAsync(request.Query, limit, host, cancellationToken));
            Add(groups, await HostRequestsAsync(request.Query, limit, request.Culture, host, cancellationToken));
            Add(groups, await SuppliersAsync(request.Query, limit, request.Culture, host, cancellationToken));
        }

        if (request.SupplierOrgId is { } supplierOrgId)
            Add(groups, await SupplierRequestsAsync(request.Query, limit, request.Culture, supplierOrgId, cancellationToken));

        // The groups are read in the order of SearchTypes.All, which is the order of the answer.
        logger.LogDebug("Global search: {Groups} group(s) with results", groups.Count);
        return new GlobalSearchResult(groups);
    }

    private static void Add(List<SearchGroup> groups, SearchGroup? group)
    {
        if (group is { Items.Count: > 0 })
            groups.Add(group);
    }

    // ─── Properties ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<SearchGroup?> PropertiesAsync(SearchQuery query, int limit, HostSearchAccess host, CancellationToken ct)
    {
        // A property belongs to the area of its mode: it is found by whoever holds property.read there.
        var modes = new List<RentalMode>();
        if (host.ShortRentProperties)
            modes.Add(RentalMode.Short);
        if (host.LongRentProperties)
            modes.Add(RentalMode.Long);
        if (modes.Count == 0)
            return null;

        var scope = host.Scope;
        var (rows, hasMore) = await TakeAsync(
            db.Properties.AsNoTracking()
                .Where(p => p.OrgId == scope.OrgId && p.IsActive && modes.Contains(p.RentalMode))
                .InScope(scope)
                .WhereKeyMatches(query, _fullText)
                .OrderByKey()
                .ThenBy(p => p.Id)
                .Select(p => new { p.Id, p.Name, p.City, p.RentalMode }),
            limit,
            ct);

        return new SearchGroup(
            SearchTypes.Property,
            rows.Select(p => new SearchHit(
                SearchTypes.Property,
                p.Id,
                p.Name,
                NullIfBlank(p.City),
                p.RentalMode == RentalMode.Long ? SearchDestinations.LongRentProperty : SearchDestinations.ShortRentProperty)).ToList(),
            hasMore);
    }

    // ─── Bookings ───────────────────────────────────────────────────────────────────────────────────────

    private async Task<SearchGroup?> BookingsAsync(SearchQuery query, int limit, HostSearchAccess host, CancellationToken ct)
    {
        if (!host.Bookings)
            return null;

        var scope = host.Scope;

        // The property and the guest are joined here, before the limit (they are of the org of the booking by construction): a
        // booking whose property was deleted is not in the page, instead of being dropped from it afterwards.
        IQueryable<Booking> ScopedBookings() => db.Bookings.AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Property.OrgId == scope.OrgId && b.Guest.OrgId == scope.OrgId)
            .InScope(scope);

        // By guest: the guests whose key matches, among the ones of the org (the bookings are then those of the scope).
        var guestIds = db.Guests
            .Where(g => g.OrgId == scope.OrgId && !g.IsDeleted && g.DataAnonymizedDate == null)
            .WhereKeyMatches(query, _fullText)
            .Select(g => g.Id);

        var byGuest = await TakeAsync(Project(ScopedBookings().Where(b => guestIds.Contains(b.GuestId))), limit, ct);

        var rows = byGuest.Rows.ToList();
        var hasMore = byGuest.HasMore;
        if (query.BookingCodePrefix is { } prefix)
        {
            // By code: a range of the prefix index. A booking found both ways is told once.
            var pattern = prefix + "%";
            var byCode = await TakeAsync(Project(ScopedBookings().Where(b => EF.Functions.Like(b.BookingCode, pattern))), limit, ct);
            rows = rows.Concat(byCode.Rows).GroupBy(row => row.Id).Select(group => group.First()).ToList();
            hasMore |= byCode.HasMore;
        }

        var ordered = rows.OrderByDescending(row => row.CheckInDate).ThenBy(row => row.Id).ToList();
        hasMore |= ordered.Count > limit;

        return new SearchGroup(
            SearchTypes.Booking,
            ordered.Take(limit).Select(ToHit).ToList(),
            hasMore);

        static IQueryable<BookingRow> Project(IQueryable<Booking> bookings) =>
            bookings
                .OrderByDescending(b => b.CheckInDate)
                .ThenBy(b => b.Id)
                .Select(b => new BookingRow(
                    b.Id,
                    b.BookingCode,
                    b.CheckInDate,
                    b.Guest.FirstName,
                    b.Guest.LastName,
                    b.Guest.IsDeleted || b.Guest.DataAnonymizedDate != null,
                    b.Property.Name));

        static SearchHit ToHit(BookingRow row)
        {
            var code = BookingCodes.Format(row.BookingCode);
            // A guest who asked to be erased leaves nothing to show but the code.
            return row.GuestHidden
                ? new SearchHit(SearchTypes.Booking, row.Id, code, NullIfBlank(row.PropertyName), SearchDestinations.ShortRentBooking)
                : new SearchHit(
                    SearchTypes.Booking,
                    row.Id,
                    FullName(row.GuestFirstName, row.GuestLastName),
                    NullIfBlank(string.Join(SubtitleSeparator, new[] { code, row.PropertyName }.Where(part => !string.IsNullOrWhiteSpace(part)))),
                    SearchDestinations.ShortRentBooking);
        }
    }

    private sealed record BookingRow(
        Guid Id,
        string BookingCode,
        DateTime CheckInDate,
        string GuestFirstName,
        string GuestLastName,
        bool GuestHidden,
        string PropertyName);

    // ─── Guests ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task<SearchGroup?> GuestsAsync(SearchQuery query, int limit, HostSearchAccess host, CancellationToken ct)
    {
        if (!host.Guests)
            return null;

        var scope = host.Scope;
        var guests = db.Guests.AsNoTracking()
            .Where(g => g.OrgId == scope.OrgId && !g.IsDeleted && g.DataAnonymizedDate == null);

        // A guest belongs to the org, not to a property: whoever reaches only some properties finds the guests of their stays.
        // The stays are a query of their own, so that the lambda below holds a subquery EF can translate (InScope is not a LINQ operator).
        if (!scope.IsOrgWide)
        {
            var stays = db.Bookings.Where(b => b.OrgId == scope.OrgId).InScope(scope);
            guests = guests.Where(g => stays.Any(b => b.GuestId == g.Id));
        }

        var (rows, hasMore) = await TakeAsync(
            guests
                .WhereKeyMatches(query, _fullText)
                .OrderByKey()
                .ThenBy(g => g.Id)
                .Select(g => new { g.Id, g.FirstName, g.LastName, g.Email }),
            limit,
            ct);

        // Only what recognises a person: the name, and the e-mail masked (m***@example.it). Never the phone or the document.
        return new SearchGroup(
            SearchTypes.Guest,
            rows.Select(g => new SearchHit(
                SearchTypes.Guest,
                g.Id,
                FullName(g.FirstName, g.LastName),
                NullIfBlank(PersonalDataMasking.MaskEmail(g.Email)),
                SearchDestinations.ShortRentGuest)).ToList(),
            hasMore);
    }

    // ─── Leases ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task<SearchGroup?> LeasesAsync(SearchQuery query, int limit, HostSearchAccess host, CancellationToken ct)
    {
        if (!host.Leases)
            return null;

        var scope = host.Scope;

        // The leases with a tenant whose name matches (the parties have no org of their own: the lease below carries it).
        var leaseIds = db.Parties
            .Where(p => p.Role == PartyRole.Tenant && p.AnonymizedAt == null)
            .WhereKeyMatches(query, _fullText)
            .Select(p => p.LeaseContractId);

        var (rows, hasMore) = await TakeAsync(
            db.LeaseContracts.AsNoTracking()
                // The property is joined before the limit, as for the bookings.
                .Where(l => l.OrgId == scope.OrgId && l.Property.OrgId == scope.OrgId && leaseIds.Contains(l.Id))
                .InScope(scope)
                .OrderByDescending(l => l.CreatedAt)
                .ThenBy(l => l.Id)
                .Select(l => new { l.Id, PropertyName = l.Property.Name }),
            limit,
            ct);
        if (rows.Count == 0)
            return null;

        // The names of the tenants of those leases, in the order they were entered (one query for the whole group).
        var ids = rows.Select(row => row.Id).ToList();
        var tenants = (await db.Parties.AsNoTracking()
                .Where(p => ids.Contains(p.LeaseContractId) && p.Role == PartyRole.Tenant && p.AnonymizedAt == null)
                .OrderBy(p => p.Position)
                .Select(p => new { p.LeaseContractId, p.FirstName, p.LastName })
                .ToListAsync(ct))
            .GroupBy(p => p.LeaseContractId)
            .ToDictionary(group => group.Key, group => group.Select(p => FullName(p.FirstName, p.LastName)).ToList());

        return new SearchGroup(
            SearchTypes.Lease,
            rows.Select(row => new SearchHit(
                SearchTypes.Lease,
                row.Id,
                TenantTitle(tenants.GetValueOrDefault(row.Id) ?? []),
                NullIfBlank(row.PropertyName),
                SearchDestinations.LongRentLease)).ToList(),
            hasMore);
    }

    private static string TenantTitle(IReadOnlyList<string> names)
    {
        var shown = string.Join(", ", names.Take(TenantNamesInTitle));
        return names.Count > TenantNamesInTitle ? $"{shown} +{names.Count - TenantNamesInTitle}" : shown;
    }

    // ─── Service requests of the host ───────────────────────────────────────────────────────────────────

    private async Task<SearchGroup?> HostRequestsAsync(
        SearchQuery query, int limit, CultureInfo culture, HostSearchAccess host, CancellationToken ct)
    {
        var contexts = new List<ServiceRequestRentalContext>();
        if (host.ShortRentProperties)
            contexts.Add(ServiceRequestRentalContext.ShortRent);
        if (host.LongRentProperties)
            contexts.Add(ServiceRequestRentalContext.LongRent);
        if (contexts.Count == 0)
            return null;

        var scope = host.Scope;
        var (rows, hasMore) = await TakeAsync(
            db.ServiceRequests.AsNoTracking()
                .Where(r => r.OrgId == scope.OrgId && contexts.Contains(r.RentalContext) && r.Property != null)
                .InScope(scope)
                .WhereKeyMatches(query, _fullText)
                .OrderByDescending(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .Select(r => new { r.Id, r.ServiceNameSnapshot, r.Category, r.RentalContext, PropertyName = r.Property!.Name }),
            limit,
            ct);

        return new SearchGroup(
            SearchTypes.ServiceRequest,
            rows.Select(r => new SearchHit(
                SearchTypes.ServiceRequest,
                r.Id,
                ServiceTitle(r.ServiceNameSnapshot, r.Category, culture),
                NullIfBlank(r.PropertyName),
                r.RentalContext == ServiceRequestRentalContext.LongRent
                    ? SearchDestinations.LongRentServiceRequest
                    : SearchDestinations.ShortRentServiceRequest)).ToList(),
            hasMore);
    }

    // ─── Suppliers the org works with ───────────────────────────────────────────────────────────────────

    private async Task<SearchGroup?> SuppliersAsync(
        SearchQuery query, int limit, CultureInfo culture, HostSearchAccess host, CancellationToken ct)
    {
        // The marketplace of the host is a short-rent matter (GET /api/suppliers asks property.read there).
        if (!host.ShortRentProperties)
            return null;

        var scope = host.Scope;

        // The suppliers that got a request from the org, within the scope: the preferred list (OrgTrustedSupplier) does not exist yet.
        var asked = db.ServiceRequests
            .Where(r => r.OrgId == scope.OrgId)
            .InScope(scope)
            .Select(r => r.SupplierOrgId);

        var (rows, hasMore) = await TakeAsync(
            db.SupplierProfiles.AsNoTracking()
                .Where(sp => sp.Status == SupplierStatus.Active && asked.Contains(sp.OrgId))
                .WhereKeyMatches(query, _fullText)
                .OrderByKey()
                .ThenBy(sp => sp.OrgId)
                .Select(sp => new { sp.OrgId, sp.LegalName, sp.CategoriesJson }),
            limit,
            ct);

        return new SearchGroup(
            SearchTypes.Supplier,
            rows.Select(sp => new SearchHit(
                SearchTypes.Supplier,
                sp.OrgId,
                sp.LegalName,
                CategoryLabels(sp.CategoriesJson, culture),
                SearchDestinations.ShortRentSupplier)).ToList(),
            hasMore);
    }

    // ─── Requests of the supplier ───────────────────────────────────────────────────────────────────────

    private async Task<SearchGroup?> SupplierRequestsAsync(
        SearchQuery query, int limit, CultureInfo culture, Guid supplierOrgId, CancellationToken ct)
    {
        var (rows, hasMore) = await TakeAsync(
            db.ServiceRequests.AsNoTracking()
                // Two parties own a request (the host's OrgId and SupplierOrgId) and it is not tenant filtered; the supplier reads the
                // comune of the host's property, which the tenant filter of Property would hide from it. Bound to SupplierOrgId here, as
                // every query of ServiceRequestRepository is.
                .IgnoreQueryFilters()
                .Where(r => r.SupplierOrgId == supplierOrgId)
                .WhereKeyMatches(query, _fullText)
                .OrderByDescending(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .Select(r => new
                {
                    r.Id,
                    r.ServiceNameSnapshot,
                    r.Category,
                    City = r.Property != null ? r.Property.City : r.LocationCity,
                }),
            limit,
            ct);

        return new SearchGroup(
            SearchTypes.SupplierRequest,
            rows.Select(r => new SearchHit(
                SearchTypes.SupplierRequest,
                r.Id,
                ServiceTitle(r.ServiceNameSnapshot, r.Category, culture),
                NullIfBlank(r.City),
                SearchDestinations.SupplierRequest)).ToList(),
            hasMore);
    }

    // ─── Shared ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first <paramref name="limit"/> rows of <paramref name="query"/> and whether there are more (one more row is read).</summary>
    private static async Task<(List<T> Rows, bool HasMore)> TakeAsync<T>(IQueryable<T> query, int limit, CancellationToken ct)
    {
        var rows = await query.Take(limit + 1).ToListAsync(ct);
        return (rows.Take(limit).ToList(), rows.Count > limit);
    }

    private static string FullName(string firstName, string lastName) => $"{firstName} {lastName}".Trim();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The name of the service the supplier wrote, or its category in the language of the caller.</summary>
    private static string ServiceTitle(string? serviceName, string category, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(serviceName) ? EmailTemplates.ServiceCategoryLabel(culture, category) : serviceName.Trim();

    /// <summary>The first categories of a supplier, in the language of the caller; null when it has none or the column is not a list.</summary>
    private static string? CategoryLabels(string? json, CultureInfo culture)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var codes = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            var labels = codes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Take(CategoriesInSubtitle)
                .Select(code => EmailTemplates.ServiceCategoryLabel(culture, code))
                .ToList();
            return labels.Count == 0 ? null : string.Join(", ", labels);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
