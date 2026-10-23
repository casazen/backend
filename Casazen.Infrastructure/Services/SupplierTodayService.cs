using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierTodayService"/>
/// <remarks>
/// Composed from the services that own the data (the request reader, the KPIs, the profile, the catalog, the agenda and, since SP-16a,
/// the cached state of the supplier's Stripe account): this class reads no table of its own, so the rules of those services (what a
/// supplier sees before the take, the explicit supplier predicate of every query) apply unchanged. The payments account and the flags
/// are optional constructor parameters (so the tests that build the service by hand stay as they are); the container always gives both,
/// and the integration test of the checklist goes through the real host.
/// </remarks>
public sealed class SupplierTodayService(
    ISupplierServiceRequestReader requests,
    ISupplierKpiService kpis,
    ISupplierService suppliers,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    TimeProvider? timeProvider = null,
    ISupplierPaymentsAccountService? paymentsAccount = null,
    IFeatureFlags? features = null) : ISupplierTodayService
{
    /// <summary>
    /// Jobs listed: far above the daily maximum a supplier can set (50), so it never bites for one day; a week can pass it, which is
    /// why the answer also carries how many jobs there are (<see cref="SupplierToday.JobsTotal"/>).
    /// </summary>
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

    public Task<SupplierToday> GetTodayAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        GetTodayAsync(supplierOrgId, 1, cancellationToken);

    public async Task<SupplierToday> GetTodayAsync(Guid supplierOrgId, int days, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, SupplierTodayLimits.MaxDays);

        var today = _clock.TodayInRomeAsDateOnly();

        // The jobs of today and of the days after it, in one read: the first in time first.
        var (jobs, jobsTotal) = await requests.ListAsync(
            supplierOrgId,
            new SupplierInboxQuery(
                JobStatuses,
                null,
                null,
                1,
                MaxJobsListed,
                SupplierInboxSort.WorkTime,
                WorkDays: new SupplierDateRange(today, today.AddDays(days - 1))),
            cancellationToken);

        var (newRequests, newTotal) = await requests.ListAsync(
            supplierOrgId,
            new SupplierInboxQuery([ServiceRequestStatus.Richiesto], null, null, 1, MaxNewRequestsListed, SupplierInboxSort.Urgency),
            cancellationToken);

        var earnings = await kpis.GetEarningsSummaryAsync(supplierOrgId, cancellationToken);

        return new SupplierToday(today, jobs, newRequests, newTotal, earnings, jobsTotal, days);
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

        // The cached state of the Stripe account (never a call to Stripe), read through the rule of GET payments/account, which is behind
        // the flag: "Verificato" (D11) is told whatever the flag says, "payments active" only while the flag is on.
        SupplierChecklistVerification? verification = null;
        bool? paymentsActive = null;
        if (paymentsAccount is not null)
        {
            var account = await paymentsAccount.GetAccountAsync(supplierOrgId, refreshFromStripe: false, cancellationToken);
            verification = new SupplierChecklistVerification(account.Verified, account.VerificationMissing);
            if (features?.IsEnabled(FeatureFlags.SupplierOnlinePayments) == true)
                paymentsActive = account.CanReceivePayments;
        }

        return new SupplierChecklist(
            dashboard.ProfileCompletionPercent,
            ProfileComplete: dashboard.ProfileCompletionPercent >= 100,
            activeServices,
            HoursConfigured: hours.ConfiguredAt is not null,
            HoursConfiguredAt: hours.ConfiguredAt,
            // As the owner's preview says: published only for an active supplier that has its address.
            ShowcasePublished: profile.Status == SupplierStatus.Active && !string.IsNullOrEmpty(profile.ShowcaseSlug),
            FirstRequestAnswered: answered,
            PaymentsActive: paymentsActive,
            Verification: verification);
    }
}
