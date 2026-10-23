using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The earnings of a supplier (SP-16a, <c>GET api/supplier/earnings</c> and its export): its completed and paid jobs with the money of each (gross,
/// commission, net, state), and the figures above them. <b>The server does every sum</b>; a screen never recomputes a commission or a net. Read-only, and
/// every read is scoped by the supplier org of the caller (the payments have two parties and no tenant filter, so the org is an explicit predicate:
/// another supplier's jobs and payments are never read).
/// </summary>
/// <remarks>
/// <para><b>What "incassato" means.</b> The figures of the money (<c>PaidThisMonthNetCents</c>, <c>CommissionThisMonthCents</c>, the series) add up the
/// payments with status <c>Paid</c> by the day they were paid, a payment recorded as received outside CasaZen included (no commission): the same figure as
/// the home (<c>GET api/supplier/dashboard/overview</c>). A payment refunded afterwards is not in them (it is no longer <c>Paid</c>); it stays in the list with
/// its state and <c>RefundedCents</c>, and its net is not rewritten. A job the host marked paid by hand has no payment record and is listed as paid
/// (gross = the declared total, no commission) but is not in those figures: no payment went through CasaZen.</para>
/// <para>Not behind the flag <c>SupplierOnlinePayments</c>: with it off there are only jobs paid outside CasaZen.</para>
/// </remarks>
public interface ISupplierEarningsService
{
    /// <summary>A page of the list, the newest first, with the figures above it.</summary>
    Task<SupplierEarningsPage> GetAsync(Guid supplierOrgId, SupplierEarningsQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rows of <paramref name="query"/> (its page is ignored) for the export, the newest first.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="SupplierEarningsErrors.ExportTooLarge"/>: more than <see cref="SupplierEarningsLimits.MaxExportRows"/> rows.
    /// </exception>
    Task<IReadOnlyList<SupplierEarningRow>> ExportAsync(Guid supplierOrgId, SupplierEarningsQuery query, CancellationToken cancellationToken = default);
}
