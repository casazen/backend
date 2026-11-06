using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The tourist tax per comune and period: what the stays owe, what the host paid and the receipts (NF-100, REP-05, CHK-07, CHK-08). It
/// extends the tourist tax report of the fiscal area (<see cref="IFiscalReportingService.GetTouristTaxReportAsync"/>) and never computes the
/// tax again: the owed amount is the one the engine recorded on the stays (BK-03), and a stay with no recorded amount is counted apart,
/// never taken as zero. The paid side is <see cref="Casazen.Core.Entities.TouristTaxRemittance"/>.
/// </summary>
/// <remarks>
/// The services never check roles: the web layer decides with the policies <c>payment.read</c> (summary, receipt) and <c>payment.write</c>
/// (record, delete). They work inside the org and the reach of the <see cref="HostScope"/> given: a payment has no property, so a caller
/// who reaches only some properties sees and records payments only for the comuni of those properties.
/// </remarks>
public interface ITouristTaxSummaryService
{
    /// <summary>
    /// The summary of <paramref name="period"/>: for each comune with stays (check-in in the period) or with payments (whose period lies
    /// inside it), the tax owed, the tax paid, the balance, the status and the payments; and the totals.
    /// </summary>
    /// <exception cref="FiscalValidationException">The period is not valid for a report (400 <c>fiscal_report_period_invalid</c>).</exception>
    Task<TouristTaxSummary> GetSummaryAsync(HostScope scope, FiscalReportPeriod period, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a payment, with its receipt when <paramref name="receipt"/> is given (read to the end, checked on its content and stored in
    /// the private bucket). Nothing is stored when a check fails.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// 422 <see cref="TouristTaxErrorCodes.ComuneUnknown"/>, <see cref="TouristTaxErrorCodes.AmountInvalid"/>,
    /// <see cref="TouristTaxErrorCodes.DateInFuture"/>, <see cref="TouristTaxErrorCodes.PeriodInvalid"/>,
    /// <see cref="TouristTaxErrorCodes.ReceiptInvalid"/> or <see cref="TouristTaxErrorCodes.TextTooLong"/>.
    /// </exception>
    Task<TouristTaxRemittanceLine> RecordRemittanceAsync(
        HostScope scope,
        RecordRemittanceRequest request,
        Stream? receipt,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a wrong entry: the row and its receipt. A payment of another org is the same <c>NotFoundException</c> as a missing one.</summary>
    Task DeleteRemittanceAsync(HostScope scope, Guid remittanceId, CancellationToken cancellationToken = default);

    /// <summary>The receipt of a payment, from the private bucket; the caller disposes the content. 404 when the payment, or its receipt, is not there.</summary>
    Task<TouristTaxReceiptFile> OpenReceiptAsync(HostScope scope, Guid remittanceId, CancellationToken cancellationToken = default);
}
