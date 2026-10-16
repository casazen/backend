using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierSlotPreviewService"/>
/// <remarks>
/// <para><b>The same planner, the same window, no gate.</b> The slots are what
/// <see cref="ISupplierAgendaService.PlanAsync(Guid, DateOnly, DateOnly, SupplierSlotQuery, CancellationToken)"/> computes for the
/// service's own query (duration, notice, weekdays), over the days <see cref="PublicSlotWindow"/> picks (the ones the public page
/// shows). What the public read adds is left out on purpose: the 30-second cache (the supplier changes its hours and looks again),
/// the condition that the service is published and the supplier active (the supplier tries a draft or a paused service), and the
/// public feature flag (the preview is the supplier's, behind its own authorization).</para>
/// <para><b>Tenancy.</b> The service is found by <see cref="ISupplierServiceCatalogService.FindForRequestAsync"/>, which scopes it to
/// the supplier org: another supplier's service, or a deleted one, is the same 404. The slots come through the agenda service. The
/// only table this class reads itself is the supplier's own profile, to ask <see cref="ISupplierComuneMatcher"/> about a comune.</para>
/// </remarks>
public sealed class SupplierSlotPreviewService(
    AppDbContext db,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    ISupplierComuneMatcher comuneMatcher,
    TimeProvider clock) : ISupplierSlotPreviewService
{
    public async Task<SupplierSlotPreview> GetAsync(
        Guid supplierOrgId,
        Guid serviceId,
        DateOnly? from,
        int? days,
        string? comune,
        CancellationToken cancellationToken = default)
    {
        var service = await catalog.FindForRequestAsync(supplierOrgId, serviceId, cancellationToken)
            ?? throw SupplierServiceCatalogErrors.ServiceNotFound(serviceId);

        var today = clock.TodayInRomeAsDateOnly();
        var rules = await agenda.GetRulesAsync(supplierOrgId, cancellationToken);
        var bookableUntil = today.AddDays(rules.HorizonDays);

        // A draft without a duration has no slot to compute: no day, not an error (the console says the service is incomplete).
        IReadOnlyList<PublicSlotDay> window = [];
        if (service.ToSlotQuery() is { } query && PublicSlotWindow.RangeOf(today, bookableUntil, from, days) is { } range)
        {
            var plans = await agenda.PlanAsync(supplierOrgId, range.First, range.Last, query, cancellationToken);
            window = plans.Select(plan => new PublicSlotDay(plan.Day, plan.Slots)).ToList();
        }

        return new SupplierSlotPreview(
            service.Id,
            service.Slug,
            service.Status,
            service.DurationMinutes ?? 0,
            bookableUntil,
            window,
            await CoverageAsync(supplierOrgId, comune, cancellationToken));
    }

    /// <summary>
    /// Whether the supplier covers <paramref name="comune"/> (an ISTAT code or a name, decision D10): only when the caller gave one.
    /// A place that cannot be recognized is outside, as for a customer's estimate.
    /// </summary>
    private async Task<SupplierPreviewCoverage?> CoverageAsync(Guid supplierOrgId, string? comune, CancellationToken cancellationToken)
    {
        var place = ComuneTarget.FromInput(comune);
        if (place.IsEmpty)
            return null;

        var profile = await db.SupplierProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(sp => sp.OrgId == supplierOrgId, cancellationToken);
        var inside = profile is not null && await comuneMatcher.CoversAsync(profile, place, cancellationToken);
        return new SupplierPreviewCoverage(inside, comune!.Trim());
    }
}
