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

        // Nothing that long, empty or with a control character (a NUL cannot reach PostgreSQL) is a slug: no lookup, the same
        // answer as for a slug nobody has.
        if (!SupplierShowcaseSlug.IsLookupable(normalized, PublicShowcaseLimits.SlugMaxLength))
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

        // The services are gated in their own statement; the response time is not, so a profile that is not active (a caller
        // that kept an old one) gets nothing at all.
        if (supplier.Status != SupplierStatus.Active)
            return new PublicSupplierExtension([], null);

        // One after the other: the reads share the request's context.
        var services = await WithUrgentAsync(supplier.OrgId, await catalog.ListPublicAsync(supplier.OrgId, cancellationToken), cancellationToken);
        var median = await kpis.GetMedianResponseMinutesAsync(supplier.OrgId, cancellationToken);
        return new PublicSupplierExtension(services, median);
    }

    public async Task<IReadOnlyList<SupplierPublicService>> ListServicesAsync(
        SupplierProfile supplier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        return await WithUrgentAsync(supplier.OrgId, await catalog.ListPublicAsync(supplier.OrgId, cancellationToken), cancellationToken);
    }

    public async Task<SupplierPublicService?> FindServiceAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        CancellationToken cancellationToken = default)
    {
        var service = await FindPlainServiceAsync(supplier, serviceSlug, cancellationToken);
        return service is null ? null : await WithUrgentAsync(supplier.OrgId, service, cancellationToken);
    }

    /// <summary>
    /// The service as the catalog has it, without the urgent terms (the supplier's settings are not read): for what does not need
    /// them, which is every read that is not about an urgent request.
    /// </summary>
    private async Task<SupplierPublicService?> FindPlainServiceAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        var normalized = SupplierShowcaseSlug.Normalize(serviceSlug);
        if (!SupplierShowcaseSlug.IsLookupable(normalized, SupplierServiceCatalogLimits.SlugMaxLength))
            return null;

        return await catalog.FindPublicAsync(supplier.OrgId, normalized, cancellationToken);
    }

    /// <summary>
    /// The services with the urgent terms the public gets (SP-04c, <see cref="SupplierPublicService.Urgent"/>): on only when the
    /// service and the supplier accept urgent requests. The supplier's settings are read only when a service has its own switch on,
    /// so a supplier that offers no urgency costs nothing more than before.
    /// </summary>
    private async Task<IReadOnlyList<SupplierPublicService>> WithUrgentAsync(
        Guid supplierOrgId,
        IReadOnlyList<SupplierPublicService> services,
        CancellationToken cancellationToken)
    {
        if (!services.Any(service => service.UrgentEnabled))
            return services;

        var urgent = await agenda.GetUrgentAsync(supplierOrgId, cancellationToken);
        return services.Select(service => service.WithUrgent(urgent)).ToList();
    }

    private async Task<SupplierPublicService> WithUrgentAsync(
        Guid supplierOrgId,
        SupplierPublicService service,
        CancellationToken cancellationToken) =>
        service.UrgentEnabled ? service.WithUrgent(await agenda.GetUrgentAsync(supplierOrgId, cancellationToken)) : service;

    public async Task<PublicSlots?> GetSlotsAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        DateOnly? from,
        int? days,
        bool urgent = false,
        CancellationToken cancellationToken = default)
    {
        var service = await FindPlainServiceAsync(supplier, serviceSlug, cancellationToken);
        if (service is null)
            return null;

        var today = clock.TodayInRomeAsDateOnly();
        if (urgent)
            return await GetUrgentSlotsAsync(supplier, service, today, from, days, cancellationToken);

        var plan = await GetPlanAsync(supplier.OrgId, service, today, urgent: false, cancellationToken);

        // The window asked for, never before today; the plan itself ends at the supplier's horizon, so the window does too. A
        // first day beyond the horizon is an empty window, not an error. The supplier's own preview (SP-03b) cuts the same way.
        IReadOnlyList<PublicSlotDay> window = [];
        if (PublicSlotWindow.RangeOf(today, plan.BookableUntil, from, days) is { } range)
            window = plan.Days.Where(day => day.Date >= range.First && day.Date <= range.Last).ToList();

        return new PublicSlots(service.Slug, service.DurationMinutes ?? 0, plan.BookableUntil, window);
    }

    /// <summary>
    /// The urgent slots of a service (SP-04c): today and tomorrow, in the supplier's urgent window, from the same planner and the
    /// same cache as the normal ones (its own entry: the key says <c>urgent</c>). No day at all when the service or the supplier does
    /// not accept urgent requests: there is nothing to offer, and the plan is not even asked for.
    /// </summary>
    private async Task<PublicSlots> GetUrgentSlotsAsync(
        SupplierProfile supplier,
        SupplierPublicService service,
        DateOnly today,
        DateOnly? from,
        int? days,
        CancellationToken cancellationToken)
    {
        var bookableUntil = today.AddDays(SupplierAgendaLimits.UrgentHorizonDays);

        var offered = await WithUrgentAsync(supplier.OrgId, service, cancellationToken);
        IReadOnlyList<PublicSlotDay> window = [];
        if (offered.Urgent.Enabled && PublicSlotWindow.RangeOf(today, bookableUntil, from, days) is { } range)
        {
            var plan = await GetPlanAsync(supplier.OrgId, offered, today, urgent: true, cancellationToken);
            window = plan.Days.Where(day => day.Date >= range.First && day.Date <= range.Last).ToList();
        }

        return new PublicSlots(service.Slug, service.DurationMinutes ?? 0, bookableUntil, window, Urgent: true);
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

        // The urgent terms (the supplier's settings) are read only for a request for an urgent slot.
        var service = request.Urgent
            ? await FindServiceAsync(supplier, serviceSlug, cancellationToken)
            : await FindPlainServiceAsync(supplier, serviceSlug, cancellationToken);
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
        bool urgent,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(supplierOrgId, service, today, urgent);
        if (cache.TryGet(key, out var cached))
            return cached;

        // An urgent plan covers today and tomorrow only (and never more than the supplier's horizon).
        var rules = await agenda.GetRulesAsync(supplierOrgId, cancellationToken);
        var horizonDays = urgent ? Math.Min(rules.HorizonDays, SupplierAgendaLimits.UrgentHorizonDays) : rules.HorizonDays;
        var bookableUntil = today.AddDays(horizonDays);

        IReadOnlyList<PublicSlotDay> days = [];
        if (service.ToSlotQuery(urgent) is { } query)
        {
            var plans = await agenda.PlanAsync(supplierOrgId, today, bookableUntil, query, cancellationToken);
            days = plans.Select(plan => new PublicSlotDay(plan.Day, plan.Slots)).ToList();
        }

        var computed = new PublicSlotPlan(bookableUntil, days);
        cache.Set(key, computed);
        return computed;
    }

    /// <summary>
    /// What the plan depends on: the supplier, the service and the three terms of it the planner reads, the day (so a plan
    /// never outlives midnight in Rome) and whether the slots are the urgent ones (<b>another plan</b>: another window, another
    /// notice, two days; without it in the key an urgent request would be served the normal slots, and the other way round). The
    /// key of a normal plan is the one it always had. The rules, hours and urgent settings of the supplier are not in the key: a
    /// change of them reaches the public within the cache TTL.
    /// </summary>
    internal static string CacheKey(Guid supplierOrgId, SupplierPublicService service, DateOnly today, bool urgent = false) =>
        $"{supplierOrgId:N}|{service.Slug}|{service.DurationMinutes}|{service.MinNoticeHours}|{service.WeekdaysMask}|{today:yyyyMMdd}"
        + (urgent ? "|urgent" : string.Empty);
}
