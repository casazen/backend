using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPublicSupplierShowcaseService"/>
/// <remarks>
/// <para><b>Who it reads for.</b> Anonymous visitors. The only table this class reads itself is <c>SupplierProfiles</c>, to
/// find an <c>Active</c> supplier by its showcase slug (not tenant-filtered: hosts of every org read active profiles). The
/// services come from <see cref="ISupplierServiceCatalogService.ListPublicAsync"/> and
/// <see cref="ISupplierServiceCatalogService.FindPublicAsync"/>, whose statements carry the explicit supplier <c>OrgId</c>
/// predicate and the <c>Active</c> status of service and supplier; the slots come from
/// <see cref="ISupplierAgendaService.PlanAsync(Guid, DateOnly, DateOnly, SupplierSlotQuery, CancellationToken)"/>; the response time from
/// <see cref="ISupplierKpiService"/>. This class never touches the tables of the catalog or of the agenda
/// (<c>SupplierServiceListingTenancyTests</c>, <c>SupplierAgendaTenancyTests</c> keep it that way), and the public types it
/// returns have no field for a person, a label, a kind of busy time or a reason of closure.</para>
/// <para><b>Time.</b> "Today" is the Europe/Rome day of the injected <see cref="TimeProvider"/>; instants are UTC.</para>
/// </remarks>
public sealed class PublicSupplierShowcaseService(
    AppDbContext db,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    ISupplierKpiService kpis,
    ISupplierComuneMatcher comuneMatcher,
    PublicSupplierSlotCache cache,
    TimeProvider clock) : IPublicSupplierShowcaseService
{
    public async Task<SupplierProfile?> FindActiveSupplierAsync(string? slug, CancellationToken cancellationToken = default)
    {
        var normalized = SupplierShowcaseSlug.Normalize(slug);

        // Nothing that long is a slug: no lookup, the same answer as for a slug nobody has.
        if (normalized.Length is 0 or > PublicShowcaseLimits.SlugMaxLength)
            return null;

        // One statement for unknown, pending and suspended alike: the cost and the answer do not tell them apart.
        return await ActiveBySlugOf(db, normalized).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The profile of the <b>active</b> supplier whose showcase slug is <paramref name="normalizedSlug"/>. Static and internal
    /// so a test can read the SQL it becomes on the PostgreSQL provider without a server.
    /// </summary>
    internal static IQueryable<SupplierProfile> ActiveBySlugOf(AppDbContext db, string normalizedSlug) =>
        db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.ShowcaseSlug == normalizedSlug && sp.Status == SupplierStatus.Active);

    public async Task<PublicSupplierExtension> GetExtensionAsync(SupplierProfile supplier, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        // One after the other: the reads share the request's context.
        var services = await catalog.ListPublicAsync(supplier.OrgId, cancellationToken);
        var median = await kpis.GetMedianResponseMinutesAsync(supplier.OrgId, cancellationToken);
        return new PublicSupplierExtension(services, median);
    }

    public Task<IReadOnlyList<SupplierPublicService>> ListServicesAsync(
        SupplierProfile supplier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        return catalog.ListPublicAsync(supplier.OrgId, cancellationToken);
    }

    public async Task<SupplierPublicService?> FindServiceAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        var normalized = SupplierShowcaseSlug.Normalize(serviceSlug);
        if (normalized.Length is 0 or > SupplierServiceCatalogLimits.SlugMaxLength)
            return null;

        return await catalog.FindPublicAsync(supplier.OrgId, normalized, cancellationToken);
    }

    public async Task<PublicSlots?> GetSlotsAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        DateOnly? from,
        int? days,
        CancellationToken cancellationToken = default)
    {
        var service = await FindServiceAsync(supplier, serviceSlug, cancellationToken);
        if (service is null)
            return null;

        var today = clock.TodayInRomeAsDateOnly();
        var plan = await GetPlanAsync(supplier.OrgId, service, today, cancellationToken);

        // The window asked for, never before today; the plan itself ends at the supplier's horizon, so the window does too. A
        // first day beyond the horizon is an empty window, not an error (and is checked before adding days to it: a made-up
        // date must not overflow).
        var count = Math.Clamp(days ?? PublicShowcaseLimits.SlotsDefaultDays, 1, PublicShowcaseLimits.SlotsMaxDays);
        var first = from is { } requested && requested > today ? requested : today;
        IReadOnlyList<PublicSlotDay> window = [];
        if (first <= plan.BookableUntil)
        {
            var last = first.AddDays(count - 1);
            window = plan.Days.Where(day => day.Date >= first && day.Date <= last).ToList();
        }

        return new PublicSlots(service.Slug, service.DurationMinutes ?? 0, plan.BookableUntil, window);
    }

    public async Task<PublicQuote?> QuoteAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        SupplierQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(serviceSlug))
            throw SupplierQuoteErrors.InvalidFields([SupplierQuoteFields.Service]);

        var service = await FindServiceAsync(supplier, serviceSlug, cancellationToken);
        if (service is null)
            return null;

        var choices = SupplierQuoteCalculator.Validate(service, request);

        // The supplier covers comuni (decision D10): the postal code is echoed, it does not decide. A place that cannot be
        // recognized is outside: the supplier decides, and nothing is promised.
        bool? inside = null;
        var coverage = PublicQuoteCoverage.Unknown;
        if (choices.Place is { IsEmpty: false } place)
        {
            inside = await comuneMatcher.CoversAsync(supplier, place, cancellationToken);
            coverage = inside.Value ? PublicQuoteCoverage.Covered : PublicQuoteCoverage.Outside;
        }

        return new PublicQuote(service, coverage, SupplierQuoteCalculator.Calculate(service, choices, inside), choices.PostalCode);
    }

    /// <summary>
    /// The plan of the service from today to the supplier's horizon: from the cache while it is fresh (30 s), else computed
    /// by the planner of the agenda (the requests with hours, the blocks, the calendar engagements, and whatever else the
    /// planning input holds) and kept. Only the days and their slots are kept: the reasons of closure stop here.
    /// </summary>
    private async Task<PublicSlotPlan> GetPlanAsync(
        Guid supplierOrgId,
        SupplierPublicService service,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(supplierOrgId, service, today);
        if (cache.TryGet(key, out var cached))
            return cached;

        var rules = await agenda.GetRulesAsync(supplierOrgId, cancellationToken);
        var bookableUntil = today.AddDays(rules.HorizonDays);

        IReadOnlyList<PublicSlotDay> days = [];
        if (service.ToSlotQuery() is { } query)
        {
            var plans = await agenda.PlanAsync(supplierOrgId, today, bookableUntil, query, cancellationToken);
            days = plans.Select(plan => new PublicSlotDay(plan.Day, plan.Slots)).ToList();
        }

        var computed = new PublicSlotPlan(bookableUntil, days);
        cache.Set(key, computed);
        return computed;
    }

    /// <summary>
    /// What the plan depends on: the supplier, the service and the three terms of it the planner reads, and the day (so a plan
    /// never outlives midnight in Rome). The rules and hours of the supplier are not in the key: a change of them reaches the
    /// public within the cache TTL.
    /// </summary>
    internal static string CacheKey(Guid supplierOrgId, SupplierPublicService service, DateOnly today) =>
        $"{supplierOrgId:N}|{service.Slug}|{service.DurationMinutes}|{service.MinNoticeHours}|{service.WeekdaysMask}|{today:yyyyMMdd}";
}
