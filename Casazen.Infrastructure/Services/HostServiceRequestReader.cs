using System.Globalization;
using System.Linq.Expressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostServiceRequestReader"/>
/// <remarks>
/// <para><b>Tenancy.</b> <c>ServiceRequests</c> has two parties and no tenant filter (the TN-2 allow-list), so every statement carries
/// the host org as a predicate and, for a restricted scope, <c>InScope</c>; the stays are read with their org as a predicate too. The
/// supplier profiles are keyed by the supplier org and not tenant-filtered, and only the two columns of the contact are read.</para>
/// <para><b>Privacy (decision D9).</b> The stay (the guest's name included) and the supplier's contact are the host's: they are
/// built here, for the host's endpoints only; the supplier's reads (<c>SupplierServiceRequestReader</c>) never touch this class.</para>
/// </remarks>
public sealed class HostServiceRequestReader(
    AppDbContext db,
    IOptions<ServiceRequestOptions> options,
    TimeProvider? timeProvider = null) : IHostServiceRequestReader
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HostServiceRequestPage> ListAsync(
        HostScope scope,
        HostServiceRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Clamp(query.Page, 1, HostServiceRequestLimits.MaxPage);
        var pageSize = Math.Clamp(query.PageSize, 1, HostServiceRequestLimits.MaxPageSize);

        var rows = Filter(Rows(scope), query).Where(HostServiceRequestListRules.InView(query.View));

        var skipped = (page - 1) * pageSize;
        var items = await Order(rows.Include(r => r.Property).Include(r => r.SupplierOrg), query.Sort)
            .Skip(skipped)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var isLastPage = items.Count < pageSize && (items.Count > 0 || skipped == 0);
        var total = isLastPage ? skipped + items.Count : await rows.CountAsync(cancellationToken);

        return new HostServiceRequestPage(items, await DescribeCoreAsync(scope.OrgId, items, cancellationToken), total, page, pageSize);
    }

    public async Task<HostServiceRequestCounts> CountAsync(
        HostScope scope,
        HostServiceRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var counts = await Filter(Rows(scope), query)
            .GroupBy(_ => 1)
            .Select(CountsSelector())
            .FirstOrDefaultAsync(cancellationToken);
        return counts ?? new HostServiceRequestCounts(0, 0, 0, 0, 0, 0);
    }

    public Task<IReadOnlyDictionary<Guid, HostServiceRequestExtras>> DescribeAsync(
        Guid hostOrgId,
        IReadOnlyCollection<ServiceRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var own = requests
            .Where(r => r.OrgId == hostOrgId && r.RentalContext == ServiceRequestRentalContext.ShortRent)
            .ToList();
        return DescribeCoreAsync(hostOrgId, own, cancellationToken);
    }

    private IQueryable<ServiceRequest> Rows(HostScope scope) =>
        db.ServiceRequests
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => r.OrgId == scope.OrgId && r.RentalContext == ServiceRequestRentalContext.ShortRent)
            .OnlyVisibleToHost()
            .InScope(scope);

    private static IQueryable<ServiceRequest> Filter(IQueryable<ServiceRequest> rows, HostServiceRequestQuery query)
    {
        if (query.Status is { } status)
            rows = rows.Where(r => r.Status == status);
        if (query.PropertyId is { } propertyId)
            rows = rows.Where(r => r.PropertyId == propertyId);
        if (query.BookingId is { } bookingId)
            rows = rows.Where(r => r.BookingId == bookingId);
        if (query.SupplierOrgId is { } supplierOrgId)
            rows = rows.Where(r => r.SupplierOrgId == supplierOrgId);
        if (query.Category is { } category)
            rows = rows.Where(r => r.Category == category);

        if (query.From is { } from)
        {
            var first = RomeCalendar.StartOfDayUtc(from);
            rows = rows.Where(r => (r.ScheduledStartUtc ?? r.CreatedAt) >= first);
        }

        if (query.To is { } to && to != DateOnly.MaxValue)
        {
            var dayAfter = RomeCalendar.StartOfDayUtc(to.AddDays(1));
            rows = rows.Where(r => (r.ScheduledStartUtc ?? r.CreatedAt) < dayAfter);
        }

        return query.Text is { Length: > 0 } text ? Match(rows, text) : rows;
    }

    private static IQueryable<ServiceRequest> Match(IQueryable<ServiceRequest> rows, string text)
    {
        var term = text.ToLowerInvariant();
        var categories = ServiceCategoryText.CodesMatching(term);
        return rows.Where(r =>
            (r.Property != null && r.Property.Name.ToLower().Contains(term))
            || r.SupplierOrg.DisplayName.ToLower().Contains(term)
            || r.SupplierOrg.Name.ToLower().Contains(term)
            || (r.ServiceNameSnapshot != null && r.ServiceNameSnapshot.ToLower().Contains(term))
            || categories.Contains(r.Category));
    }

    private static IOrderedQueryable<ServiceRequest> Order(IQueryable<ServiceRequest> rows, HostServiceRequestSort? sort)
    {
        var descending = sort?.Descending ?? true;
        switch (sort?.Key ?? HostServiceRequestSortKey.Created)
        {
            case HostServiceRequestSortKey.Scheduled:
                var byTime = rows.OrderBy(r => r.ScheduledStartUtc == null);
                return (descending
                        ? byTime.ThenByDescending(r => r.ScheduledStartUtc)
                        : byTime.ThenBy(r => r.ScheduledStartUtc))
                    .ThenByDescending(r => r.CreatedAt)
                    .ThenBy(r => r.Id);

            case HostServiceRequestSortKey.Status:
                var needsHost = HostServiceRequestTodoRules.NeedTheHost();
                var rank = HostServiceRequestListRules.StatusRank();
                var byStatus = descending
                    ? rows.OrderBy(needsHost).ThenByDescending(rank)
                    : rows.OrderByDescending(needsHost).ThenBy(rank);
                return byStatus
                    .ThenBy(r => r.ScheduledStartUtc == null)
                    .ThenBy(r => r.ScheduledStartUtc)
                    .ThenByDescending(r => r.CreatedAt)
                    .ThenBy(r => r.Id);

            default:
                return (descending
                        ? rows.OrderByDescending(r => r.CreatedAt)
                        : rows.OrderBy(r => r.CreatedAt))
                    .ThenBy(r => r.Id);
        }
    }

    private static Expression<Func<IGrouping<int, ServiceRequest>, HostServiceRequestCounts>> CountsSelector()
    {
        var group = Expression.Parameter(typeof(IGrouping<int, ServiceRequest>), "g");
        var countAll = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 1)
            .MakeGenericMethod(typeof(ServiceRequest));
        var countWhere = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(ServiceRequest));

        Expression CountOf(HostServiceRequestView view) =>
            Expression.Call(countWhere, group, HostServiceRequestListRules.InView(view));

        var constructor = typeof(HostServiceRequestCounts).GetConstructors().Single();
        var body = Expression.New(
            constructor,
            Expression.Call(countAll, group),
            CountOf(HostServiceRequestView.Pending),
            CountOf(HostServiceRequestView.Active),
            CountOf(HostServiceRequestView.ToPay),
            CountOf(HostServiceRequestView.Done),
            CountOf(HostServiceRequestView.NeedsAction));
        return Expression.Lambda<Func<IGrouping<int, ServiceRequest>, HostServiceRequestCounts>>(body, group);
    }

    private async Task<IReadOnlyDictionary<Guid, HostServiceRequestExtras>> DescribeCoreAsync(
        Guid hostOrgId,
        IReadOnlyCollection<ServiceRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
            return new Dictionary<Guid, HostServiceRequestExtras>();

        var stays = await StaysAsync(hostOrgId, requests, cancellationToken);
        var contacts = await ContactsAsync(requests, cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        var remindIntervalHours = options.Value.RemindIntervalHours;

        return requests.ToDictionary(
            r => r.Id,
            r => new HostServiceRequestExtras(
                string.IsNullOrWhiteSpace(r.Property?.City) ? null : r.Property.City,
                r.BookingId is { } bookingId && stays.TryGetValue(bookingId, out var stay) ? stay : null,
                r.TakenAt is not null && contacts.TryGetValue(r.SupplierOrgId, out var contact) ? contact : null,
                HostServiceRequestActions.Of(
                    new HostRequestFacts(r.Status, r.ProposedStartUtc != null, r.FinalAmountNeedsConfirmation, r.PaymentMode, r.LastRemindedAt),
                    now,
                    remindIntervalHours)));
    }

    private async Task<Dictionary<Guid, HostRequestStay>> StaysAsync(
        Guid hostOrgId,
        IReadOnlyCollection<ServiceRequest> requests,
        CancellationToken cancellationToken)
    {
        var bookingIds = requests.Where(r => r.BookingId != null).Select(r => r.BookingId!.Value).Distinct().ToList();
        if (bookingIds.Count == 0)
            return [];

        var rows = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(b => b.OrgId == hostOrgId && bookingIds.Contains(b.Id))
            .Select(b => new { b.Id, b.CheckInDate, b.CheckOutDate, b.Guest.FirstName, b.Guest.LastName })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            b => b.Id,
            b => new HostRequestStay(
                b.Id,
                $"{b.FirstName} {b.LastName}".Trim(),
                DateOnly.FromDateTime(b.CheckInDate),
                DateOnly.FromDateTime(b.CheckOutDate)));
    }

    private async Task<Dictionary<Guid, HostRequestSupplierContact>> ContactsAsync(
        IReadOnlyCollection<ServiceRequest> requests,
        CancellationToken cancellationToken)
    {
        var supplierOrgIds = requests.Where(r => r.TakenAt != null).Select(r => r.SupplierOrgId).Distinct().ToList();
        if (supplierOrgIds.Count == 0)
            return [];

        var rows = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => supplierOrgIds.Contains(sp.OrgId))
            .Select(sp => new { sp.OrgId, sp.Phone, sp.Email })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            sp => sp.OrgId,
            sp => new HostRequestSupplierContact(
                string.IsNullOrWhiteSpace(sp.Phone) ? null : sp.Phone.Trim(),
                string.IsNullOrWhiteSpace(sp.Email) ? null : sp.Email.Trim()));
    }
}

/// <summary>
/// The words a service category is found by when a host searches its interventions: its code and its label in Italian and in English,
/// lower case (the page shows the label, not the code, so a host types "pulizie"). Built on the first search, not with the type (it
/// reads the e-mail texts), and <see cref="LazyThreadSafetyMode.PublicationOnly"/> so that a failure is tried again.
/// </summary>
internal static class ServiceCategoryText
{
    private static readonly Lazy<IReadOnlyList<(string Code, string[] Words)>> Words = new(
        () => ServiceCategories.All
            .Select(code => (
                code,
                new[]
                {
                    code,
                    EmailTemplates.ServiceCategoryLabel(CultureInfo.GetCultureInfo("it-IT"), code).ToLowerInvariant(),
                    EmailTemplates.ServiceCategoryLabel(CultureInfo.GetCultureInfo("en"), code).ToLowerInvariant(),
                }))
            .ToArray(),
        LazyThreadSafetyMode.PublicationOnly);

    public static string[] CodesMatching(string lowerTerm) =>
        Words.Value
            .Where(category => category.Words.Any(word => word.Contains(lowerTerm, StringComparison.Ordinal)))
            .Select(category => category.Code)
            .ToArray();
}
