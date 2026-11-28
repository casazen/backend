using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The invoice of the property manager to an owner (E14/F01/US01/T01, NF-114, D9, decision P1 of 2026-11-14): a <see cref="FiscalDocument"/> with the purpose
/// <see cref="FiscalDocumentPurpose.OwnerInvoice"/>, made from a version of the owner statement, with one line for each thing the agency charges: its
/// commission (one line per property), the recharges (the flat fees of the mandate, the approved charges, the card fees passed on, and the commissions of the
/// portals whose account the agency holds when the organization recharges them, ⚖ I10, I11) and the expenses advanced in the name of the owner (outside the VAT).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One per owner and period.</b> A live invoice (neither discarded, nor credited in full) holds its owner and month (unique index of E11): a second draft
/// of the same month is the same document, made again from the version asked for; an issued one is 409 <c>document_already_issued</c>.</item>
/// <item><b>The VAT of the mandate (⚖ R4).</b> A mandate «+ IVA» (<see cref="OwnerMandate.CommissionIncludesVat"/> false, the default) makes the amounts of the
/// ledger the taxable amounts and adds the VAT; «IVA inclusa» makes them the gross and takes the VAT out. The rates are those of the tax rule in force on the
/// last day of the period (CH 8.1, IT 22, or the flat-rate regime with N2.2 and the stamp duty).</item>
/// <item><b>The issue</b> goes through the issue of the fiscal documents (numbering in the series <see cref="OrgFiscalIssuerProfile.OwnerInvoiceSeries"/>, snapshot of
/// the issuer, rule version, the Italian XML). A VAT added on top is posted to the owner ledger in the same transaction (<see cref="OwnerLedgerEntryType.AgencyVat"/>),
/// once; the draft or flagged version of the month is generated again, an approved or published one is told by the readiness
/// (<c>owner_invoice_after_approval</c>). Its credit note, and the discard of its draft, are made here too (<see cref="CreateCreditNoteAsync"/>,
/// <see cref="DiscardDraftAsync"/>): they write to the owner ledger or free the month, so they need the same permission as the issue.</item>
/// <item><b>Tenant.</b> Every method names the org: a version or a document of another org is 404. The web layer decides who may call (the whole organization).</item>
/// </list>
/// </remarks>
public interface IOwnerInvoiceService
{
    Task<FiscalDocument> CreateDraftAsync(Guid orgId, Guid versionId, string? userId, CancellationToken ct = default);

    /// <summary>
    /// The draft issued, after the confirmation (422 <c>document_confirmation_required</c>): numbered in the owner-invoice series, its VAT on top posted once to the
    /// owner ledger. 404 <c>fiscal_document_not_found</c> (another org, not an owner invoice); 422 <c>document_not_draft</c>; 409 <c>document_draft_outdated</c>
    /// when the version, the mandate, the holders or the rule would give other amounts now (make the draft again first).
    /// </summary>
    Task<FiscalDocument> IssueAsync(Guid orgId, Guid documentId, bool confirm, string? userId, CancellationToken ct = default);

    /// <summary>
    /// A credit note of an issued owner invoice, issued at once in its series after the confirmation (422 <c>document_confirmation_required</c>): the whole invoice by
    /// default, never more than what is left to credit (422 <c>credit_amount_exceeds</c>). It gives the VAT the invoice posted back to the owner ledger in the same
    /// transaction and generates again the draft or flagged statement version of the month; an invoice credited in full frees its owner and month. 404
    /// <c>fiscal_document_not_found</c> (another org, not an owner invoice); 422 <c>document_not_creditable</c>. The one way to credit an owner invoice: the
    /// endpoints of the guest documents refuse it (422 <c>document_owner_invoice</c>).
    /// </summary>
    Task<FiscalDocument> CreateCreditNoteAsync(Guid orgId, Guid documentId, decimal? amount, string reason, bool confirm, string? userId, CancellationToken ct = default);

    Task DiscardDraftAsync(Guid orgId, Guid documentId, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerInvoiceRow>> ListAsync(Guid orgId, OwnerInvoiceCriteria criteria, CancellationToken ct = default);

    /// <summary>
    /// One owner invoice with its lines, as the draft answers them (FIX/RES-OWNERS): a draft is read again after a reload without making it again, and an issued invoice or a
    /// credit note is read as it was issued. Never written, never numbered. 404 <c>fiscal_document_not_found</c> for a document of another org and for a document that is not
    /// an owner invoice (a document of a guest answers 404 here, whoever asks).
    /// </summary>
    Task<FiscalDocument> GetAsync(Guid orgId, Guid documentId, CancellationToken ct = default);

    Task<OwnerInvoiceGeneration> CreateDraftsForPeriodAsync(Guid orgId, int year, int month, string? userId, CancellationToken ct = default);
}

public sealed record OwnerInvoiceCriteria(int? Year = null, int? Month = null, Guid? OwnerId = null);

public sealed record OwnerInvoiceRow(
    Guid Id,
    Guid OwnerId,
    string? OwnerName,
    int PeriodYear,
    int PeriodMonth,
    Guid? StatementVersionId,
    string? Number,
    DateOnly? IssueDate,
    FiscalDocumentStatus Status,
    FiscalDocumentKind Kind,
    decimal TaxableBase,
    decimal VatAmount,
    decimal StampDuty,
    decimal Total,
    string Currency,
    bool CreditedInFull,
    OwnerInvoiceSettlement? Settlement);

public sealed record OwnerInvoiceSettlement(string State, decimal AmountDue);

public static class OwnerInvoiceSettlements
{
    public const string Offset = "offset";

    public const string ToPay = "to_pay";

    public const string Credited = "credited";

    /// <summary>
    /// The settlement of an invoice of which <paramref name="payable"/> is left (its total less its credit notes), for an owner whose ledger had
    /// <paramref name="openingBalance"/> before the period and <paramref name="closingBalance"/> at its end, the VAT of the invoice included (positive: the agency
    /// owes the owner). Due = how much the debt of the owner grew in the period, never below 0 nor above <paramref name="payable"/>.
    /// </summary>
    public static OwnerInvoiceSettlement Of(decimal payable, decimal openingBalance, decimal closingBalance)
    {
        if (payable <= 0m)
            return new OwnerInvoiceSettlement(Credited, 0m);

        var grown = Math.Max(0m, -closingBalance) - Math.Max(0m, -openingBalance);
        var due = Math.Min(payable, Math.Max(0m, grown));
        return due > 0m ? new OwnerInvoiceSettlement(ToPay, due) : new OwnerInvoiceSettlement(Offset, 0m);
    }
}

public sealed record OwnerInvoiceGeneration(
    int Year,
    int Month,
    int Owners,
    int Created,
    int Updated,
    int AlreadyIssued,
    int NothingToInvoice,
    IReadOnlyList<OwnerInvoiceFailure> Failed);

public sealed record OwnerInvoiceFailure(Guid OwnerId, string Code);

public static class OwnerInvoiceErrors
{
    public const string VersionSuperseded = "owner_invoice_version_superseded";

    public const string NothingToInvoice = "owner_invoice_nothing_to_invoice";

    public const string UseOwnerInvoices = "document_owner_invoice";
}
