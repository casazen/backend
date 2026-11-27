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
/// (record, delete). They work inside the org and the reach of the <see cref="HostScope"/> given. A payment belongs to the org and to no
/// property, so a caller who reaches only some properties gets the owed side of those properties and no payment at all
/// (<see cref="TouristTaxSummary.PaymentsHidden"/>), and has no part in recording, deleting or opening one (403
/// <see cref="TouristTaxErrorCodes.PaymentsNeedWholeOrg"/>). Only the properties in Italy count (<see cref="Casazen.Core.TouristTax.TouristTaxComune.IsItalianCountry"/>):
/// the rates are those of the Italian comuni and every amount is in euros, so a house abroad is neither summed nor payable.
/// </remarks>
public interface ITouristTaxSummaryService
{
    /// <summary>
    /// The summary of <paramref name="period"/>: for each comune with stays (check-in in the period) or with payments (whose period lies
    /// inside it), the tax owed, the tax paid, the balance, the status and the payments; and the totals. For a scope that does not reach the
    /// whole org only the owed side of the properties it reaches (<see cref="TouristTaxSummary.PaymentsHidden"/>).
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
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException">
    /// 403 <see cref="TouristTaxErrorCodes.PaymentsNeedWholeOrg"/>: the scope does not reach the whole org. Nothing is read or stored.
    /// </exception>
    Task<TouristTaxRemittanceLine> RecordRemittanceAsync(
        HostScope scope,
        RecordRemittanceRequest request,
        Stream? receipt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a wrong entry: the row and its receipt. A payment of another org is the same <c>NotFoundException</c> as a missing one. 403
    /// <see cref="TouristTaxErrorCodes.PaymentsNeedWholeOrg"/> when the scope does not reach the whole org.
    /// </summary>
    Task DeleteRemittanceAsync(HostScope scope, Guid remittanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The receipt of a payment, from the private bucket; the caller disposes the content. 404 when the payment, or its receipt, is not there;
    /// 403 <see cref="TouristTaxErrorCodes.PaymentsNeedWholeOrg"/> when the scope does not reach the whole org.
    /// </summary>
    Task<TouristTaxReceiptFile> OpenReceiptAsync(HostScope scope, Guid remittanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The annual declaration data of the tourist tax per comune for <paramref name="year"/> (E18/F01/US02/T03, NF-117): the summary of the year as it is (nights, guests, owed, remitted with the
    /// days and references of the payments, the difference), the stays that have a tax and the stays that have none with the reason, and the exempt guests by reason. Nothing is recomputed and
    /// nothing is filed. A scope that does not reach the whole org is 403 <see cref="TouristTaxErrorCodes.YearEndNeedsWholeOrg"/>, before anything is read.
    /// </summary>
    /// <exception cref="FiscalValidationException">The year is not valid for a report (400 <c>fiscal_report_period_invalid</c>).</exception>
    /// <param name="scope">The reach of the caller.</param>
    /// <param name="year">The year of the check-ins.</param>
    /// <param name="withExemptionReasons">
    /// True only for a caller that holds <c>guest.read</c>: the reason of an exemption can reveal a health condition (art. 9 GDPR), so without it the exempt guests are counted by the kind of
    /// their exemption alone (<c>minor_age</c>, or <see cref="TouristTaxExemptionKinds.Restricted"/>) and no reason leaves.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<TouristTaxDeclaration> GetDeclarationAsync(HostScope scope, int year, bool withExemptionReasons, CancellationToken cancellationToken = default);

    /// <summary>
    /// The account of the agent for <paramref name="year"/> (E18/F01/US02/T03, «conto giudiziale»): for each comune the tax collected and remitted by month or quarter (the summary of each
    /// period), the line of the year and every payment with its day and reference. Same reach and errors as <see cref="GetDeclarationAsync"/>.
    /// </summary>
    Task<TouristTaxAgentAccount> GetAgentAccountAsync(HostScope scope, int year, TouristTaxAccountPeriod by, CancellationToken cancellationToken = default);

    /// <summary>
    /// The yearly count of the nights per property in Ticino for <paramref name="year"/> (E18/F01/US02/T03, due by 5 January of the next year): the nights of the stays of the year, taxed and
    /// untaxed by their documents, and the Swiss amounts from the documents issued (E12). Same reach and errors as <see cref="GetDeclarationAsync"/>.
    /// </summary>
    Task<TicinoYearlyCount> GetTicinoYearlyCountAsync(HostScope scope, int year, CancellationToken cancellationToken = default);
}
