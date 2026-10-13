using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierTodayService"/>
/// <remarks>
/// Composed from the services that own the data (the request reader, the KPIs, the profile, the catalog and the agenda): this
/// class reads no table of its own, so the rules of those services (what a supplier sees before the take, the explicit supplier
/// predicate of every query) apply unchanged.
/// </remarks>
public sealed class SupplierTodayService(
    ISupplierServiceRequestReader requests,
    ISupplierKpiService kpis,
    ISupplierService suppliers,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    TimeProvider? timeProvider = null) : ISupplierTodayService
{
    /// <summary>Jobs of the day listed: far above the daily maximum a supplier can set (50).</summary>
    private const int MaxJobsListed = 50;

    /// <summary>New requests listed on the home (the console shows the first three); the total is given apart.</summary>
    private const int MaxNewRequestsListed = 10;

    private static readonly ServiceRequestStatus[] JobStatuses =
    [
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
        ServiceRequestStatus.Completato,
        ServiceRequestStatus.Pagato,
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<SupplierToday> GetTodayAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var today = _clock.TodayInRomeAsDateOnly();

        var (jobs, _) = await requests.ListAsync(
            supplierOrgId,
            new SupplierInboxQuery(JobStatuses, null, null, 1, MaxJobsListed, SupplierInboxSort.WorkTime, When: SupplierInboxWhen.Today),
            cancellationToken);

        var (newRequests, newTotal) = await requests.ListAsync(
            supplierOrgId,
            new SupplierInboxQuery([ServiceRequestStatus.Richiesto], null, null, 1, MaxNewRequestsListed, SupplierInboxSort.Urgency),
            cancellationToken);

        var earnings = await kpis.GetEarningsSummaryAsync(supplierOrgId, cancellationToken);

        return new SupplierToday(today, jobs, newRequests, newTotal, earnings);
    }

    public async Task<SupplierChecklist?> GetChecklistAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var profile = await suppliers.GetProfileAsync(supplierOrgId, cancellationToken);
        if (profile is null)
            return null;

        var dashboard = await suppliers.GetDashboardStatsAsync(supplierOrgId, cancellationToken);
        var activeServices = await catalog.CountActiveAsync(supplierOrgId, cancellationToken);
        var hours = await agenda.GetHoursAsync(supplierOrgId, cancellationToken);
        var answered = await kpis.HasAnsweredARequestAsync(supplierOrgId, cancellationToken);

        return new SupplierChecklist(
            dashboard.ProfileCompletionPercent,
            ProfileComplete: dashboard.ProfileCompletionPercent >= 100,
            activeServices,
            HoursConfigured: hours.ConfiguredAt is not null,
            HoursConfiguredAt: hours.ConfiguredAt,
            // As the owner's preview says: published only for an active supplier that has its address.
            ShowcasePublished: profile.Status == SupplierStatus.Active && !string.IsNullOrEmpty(profile.ShowcaseSlug),
            FirstRequestAnswered: answered,
            // The payments of the suppliers are not available yet (SP-14, SP-15): not a "no", so no value.
            PaymentsActive: null);
    }
}
