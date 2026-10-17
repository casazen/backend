using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierEarningsService"/>
/// <remarks>
/// <para>Composed from the read models of the console, so the rules of those (the explicit supplier predicate on every statement, only the columns
/// the console needs) apply unchanged: this class reads no table of its own. The rows, what is to be collected and the payments that were paid come
/// from <see cref="ISupplierPaymentReadModels"/>, the commission that applies now from the same reading the admin tools use
/// (<see cref="ISupplierPaymentAdminService.GetCommissionAsync"/>: the supplier's own override in force, else the platform percentage).</para>
/// <para>The money of the month and the series come from <b>one read</b> of the payments paid since the first of the oldest month of the series, so
/// the figure of the month is the last month of the series by construction. "Today" and the months are Europe/Rome; instants are UTC.</para>
/// </remarks>
public sealed class SupplierEarningsService(
    ISupplierPaymentReadModels payments,
    ISupplierPaymentAdminService commissions,
    IFeatureFlags features,
    TimeProvider? timeProvider = null) : ISupplierEarningsService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<SupplierEarningsPage> GetAsync(
        Guid supplierOrgId,
        SupplierEarningsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (items, total) = await payments.ListEarningsAsync(supplierOrgId, query, cancellationToken);
        var totals = await TotalsAsync(supplierOrgId, cancellationToken);
        return new SupplierEarningsPage(items, total, totals);
    }

    public async Task<IReadOnlyList<SupplierEarningRow>> ExportAsync(
        Guid supplierOrgId,
        SupplierEarningsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // One row more than the limit says that there are too many, without reading them all.
        var (items, _) = await payments.ListEarningsAsync(
            supplierOrgId,
            query with { Page = 1, PageSize = SupplierEarningsLimits.MaxExportRows + 1 },
            cancellationToken);
        if (items.Count > SupplierEarningsLimits.MaxExportRows)
            throw SupplierEarningsErrors.TooLargeToExport();

        return items;
    }

    private async Task<SupplierEarningsTotals> TotalsAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var months = SupplierEarningMonths.Last(RomeCalendar.DateInRome(now), SupplierEarningsLimits.SeriesMonths);

        var paid = await payments.PaidSinceAsync(supplierOrgId, months[0].FromUtc, cancellationToken);
        var series = months
            .Select(month => new SupplierEarningsMonth(
                month.Month,
                paid.Where(p => p.PaidAt >= month.FromUtc && p.PaidAt < month.ToUtc).Sum(p => (long)p.NetCents)))
            .ToList();
        var thisMonth = months[^1];
        var paidThisMonth = paid.Where(p => p.PaidAt >= thisMonth.FromUtc && p.PaidAt < thisMonth.ToUtc).ToList();

        var toCollect = await payments.ToCollectAsync(supplierOrgId, cancellationToken);
        var late = await payments.CountLateAsync(supplierOrgId, cancellationToken);
        var setting = await commissions.GetCommissionAsync(supplierOrgId, cancellationToken);
        var online = features.IsEnabled(FeatureFlags.SupplierOnlinePayments)
                     && await payments.HasPaymentsAsync(supplierOrgId, cancellationToken);

        return new SupplierEarningsTotals(
            toCollect,
            series[^1].NetCents,
            paidThisMonth.Sum(p => (long)p.CommissionCents),
            late,
            series,
            // A free period is an override of 0 % that ends: the date is told only then.
            new SupplierCommissionInfo(
                setting.EffectivePercent,
                setting is { OverrideActive: true, OverridePercent: 0m } ? setting.OverrideUntil : null),
            online);
    }
}
