using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// What the console needs to know about the payment of a request to show its summary (SP-04e): the part of the request the rules of
/// the payment actions look at. Built by the reader of the requests from the columns it already reads.
/// </summary>
/// <param name="RequestId">The request.</param>
/// <param name="Status">Where it is.</param>
/// <param name="PaymentMode">How it is paid, fixed when the supplier took it.</param>
/// <param name="FinalAmountCents">The total the supplier declared when it completed the work.</param>
/// <param name="FinalAmountNeedsConfirmation">The host has to confirm the amount first (decision D7).</param>
/// <param name="PayerHasEmail">The org that pays has an address to send the payment link to (never true for a customer of the showcase).</param>
public sealed record SupplierPaymentSummaryInput(
    Guid RequestId,
    ServiceRequestStatus Status,
    ServiceRequestPaymentMode PaymentMode,
    int? FinalAmountCents,
    bool FinalAmountNeedsConfirmation,
    bool PayerHasEmail);

/// <summary>Gross, commission and net of a set of payments, in cents.</summary>
/// <param name="GrossCents">What the payers paid.</param>
/// <param name="CommissionCents">What CasaZen kept (0 for a payment without commission).</param>
/// <param name="NetCents">What the supplier received before Stripe's own fees.</param>
public sealed record SupplierPaymentTotals(long GrossCents, long CommissionCents, long NetCents)
{
    /// <summary>No payment at all.</summary>
    public static SupplierPaymentTotals None { get; } = new(0, 0, 0);
}

/// <summary>
/// The reads of the payments of a request for the supplier console (SP-04e): the summary of each request, and the figures of the home.
/// <c>ServiceRequestPayments</c> has two parties and no tenant filter, so every method carries the supplier org of the caller as an
/// explicit predicate (<c>ServiceRequestPaymentTenancyTests</c>): another supplier's payments are never read. Read-only.
/// </summary>
public interface ISupplierPaymentReadModels
{
    /// <summary>
    /// The payment summary of each request of <paramref name="requests"/> that has one, by request id (<see cref="SupplierJobPayment"/>):
    /// the ones that have a live payment (not canceled) or are <c>Completato</c>. The supplier-level facts the rules need (the flag, the
    /// status of the supplier, its Terms, its Stripe account) are read once.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, SupplierJobPayment>> SummariesAsync(
        Guid supplierOrgId,
        IReadOnlyCollection<SupplierPaymentSummaryInput> requests,
        CancellationToken cancellationToken = default);

    /// <summary>How many payments the supplier asked for are late and still owed (<c>paymentsLate</c> of the inbox counts).</summary>
    Task<int> CountLateAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>True when the supplier has any payment (not canceled): the console talks about "incassi" only then.</summary>
    Task<bool> HasPaymentsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payments that were <b>paid</b> (<see cref="ServicePaymentStatus.Paid"/>, a payment recorded as received outside CasaZen
    /// included) with <c>PaidAt</c> in <c>[fromUtc, toUtc)</c>.
    /// </summary>
    Task<SupplierPaymentTotals> PaidBetweenAsync(
        Guid supplierOrgId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What the supplier will receive from the payments of <paramref name="requestIds"/> that are still to be collected (asked, in
    /// progress, failed or under review), by request id, in cents: the net of the live payment. A request with no such payment is not in
    /// the answer.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> NetToCollectAsync(
        Guid supplierOrgId,
        IReadOnlyCollection<Guid> requestIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A page of the supplier's completed and paid jobs with the money of each (SP-16a, <see cref="SupplierEarningRow"/>), the newest first, and how
    /// many there are in all: the requests (<c>Completato</c>, <c>Pagato</c>) joined with their live payment (the one that is not canceled), if any,
    /// filtered and paged by the database. <see cref="SupplierEarningsQuery.PageSize"/> is not clamped here (the export asks for one more than its limit).
    /// </summary>
    Task<(IReadOnlyList<SupplierEarningRow> Items, int Total)> ListEarningsAsync(
        Guid supplierOrgId,
        SupplierEarningsQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What is still to be collected now (SP-16a): for each completed job, the net of its live payment when it is asked for, in progress, failed or under
    /// review, else the total the supplier declared (nothing is added for a job with no declared total, but it counts as a job). The same definition as
    /// <c>toCollectCents</c> of the home overview.
    /// </summary>
    Task<SupplierToCollect> ToCollectAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payments that were <b>paid</b> (<see cref="ServicePaymentStatus.Paid"/>) with <c>PaidAt</c> at or after <paramref name="sinceUtc"/>, as
    /// when, commission and net: what the series of the earnings and the figures of the month are made of, from one read.
    /// </summary>
    Task<IReadOnlyList<SupplierPaidPayment>> PaidSinceAsync(
        Guid supplierOrgId,
        DateTime sinceUtc,
        CancellationToken cancellationToken = default);
}
