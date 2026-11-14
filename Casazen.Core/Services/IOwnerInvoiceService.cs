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
/// (<c>owner_invoice_after_approval</c>).</item>
/// <item><b>Tenant.</b> Every method names the org: a version or a document of another org is 404. The web layer decides who may call (the whole organization).</item>
/// </list>
/// </remarks>
public interface IOwnerInvoiceService
{
    /// <summary>
    /// The draft of the owner invoice of a statement version, or the draft the owner and period already have, made again from that version. 404
    /// <c>owner_statement_version_not_found</c>; 409 <c>owner_invoice_version_superseded</c>, <c>document_already_issued</c>; 422
    /// <c>owner_invoice_nothing_to_invoice</c>, <c>document_rule_not_configured</c>, <c>issuer_profile_incomplete</c>, <c>document_invoice_data_missing</c>.
    /// </summary>
    Task<FiscalDocument> CreateDraftAsync(Guid orgId, Guid versionId, string? userId, CancellationToken ct = default);

    /// <summary>
    /// The draft issued, after the confirmation (422 <c>document_confirmation_required</c>): numbered in the owner-invoice series, its VAT on top posted once to the
    /// owner ledger. 404 <c>fiscal_document_not_found</c> (another org, not an owner invoice); 422 <c>document_not_draft</c>; 409 <c>document_draft_outdated</c>
    /// when the version, the mandate, the holders or the rule would give other amounts now (make the draft again first).
    /// </summary>
    Task<FiscalDocument> IssueAsync(Guid orgId, Guid documentId, bool confirm, string? userId, CancellationToken ct = default);

    /// <summary>The owner invoices of the org (drafts and issued ones, not the discarded ones nor the credit notes), the newest period first.</summary>
    Task<IReadOnlyList<OwnerInvoiceRow>> ListAsync(Guid orgId, OwnerInvoiceCriteria criteria, CancellationToken ct = default);

    /// <summary>
    /// The drafts of a period for every owner with a statement version of it that is not superseded: made, or made again, with the counts; an owner whose
    /// invoice is issued is left alone, one with nothing to invoice is counted. 422 <c>owner_period_invalid</c>; an issuer that cannot issue (profile, rule) is
    /// 422 for the whole call, before anything is written.
    /// </summary>
    Task<OwnerInvoiceGeneration> CreateDraftsForPeriodAsync(Guid orgId, int year, int month, string? userId, CancellationToken ct = default);
}

/// <summary>The filters of the list: a period (year and month together) and an owner.</summary>
public sealed record OwnerInvoiceCriteria(int? Year = null, int? Month = null, Guid? OwnerId = null);

/// <summary>One owner invoice of the list: the stored amounts in <see cref="Currency"/> and, once issued, how it is paid.</summary>
/// <param name="Settlement">Offset in the statement, to pay (with the amount) or credited; <c>null</c> for a draft.</param>
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

/// <summary>How an issued owner invoice is paid: <see cref="OwnerInvoiceSettlements"/> and the amount the owner still has to pay.</summary>
public sealed record OwnerInvoiceSettlement(string State, decimal AmountDue);

/// <summary>
/// Whether an owner invoice is offset in the statement or left to pay (E14/F01/US01/T02): the one rule of the list, the PDF and the QR-bill. The statement
/// offsets the invoice with what the agency owes the owner; what the debt of the owner grew by in the period, up to what is left to pay of the invoice, is to pay.
/// </summary>
public static class OwnerInvoiceSettlements
{
    /// <summary>The statement of the period covers the invoice: nothing to pay.</summary>
    public const string Offset = "offset";

    /// <summary>The statement leaves an amount for the owner to pay.</summary>
    public const string ToPay = "to_pay";

    /// <summary>Credit notes credited the whole invoice.</summary>
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

/// <summary>What the drafts of a period did: owners with a version, drafts made, drafts made again, invoices already issued, owners with nothing to invoice.</summary>
/// <param name="Failed">The owners whose draft could not be made, with the code of the reason (the recipient data missing, an invalid version...).</param>
public sealed record OwnerInvoiceGeneration(
    int Year,
    int Month,
    int Owners,
    int Created,
    int Updated,
    int AlreadyIssued,
    int NothingToInvoice,
    IReadOnlyList<OwnerInvoiceFailure> Failed);

/// <summary>An owner whose draft could not be made, and why (a stable code).</summary>
public sealed record OwnerInvoiceFailure(Guid OwnerId, string Code);

/// <summary>The stable codes of the owner invoices, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class OwnerInvoiceErrors
{
    /// <summary>409: the version asked for was superseded by a later one: the invoice is made from the version of today.</summary>
    public const string VersionSuperseded = "owner_invoice_version_superseded";

    /// <summary>422: the version has no commission, recharge or expense of the agency: there is nothing to invoice.</summary>
    public const string NothingToInvoice = "owner_invoice_nothing_to_invoice";

    /// <summary>
    /// 422: an owner invoice is made again and issued from the owner invoices (<c>api/owner-invoices</c>, with <c>agency.statement.manage</c> and its flag), never
    /// from the endpoints of the documents of the guests.
    /// </summary>
    public const string UseOwnerInvoices = "document_owner_invoice";
}
