using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The files of the agency financial chain (NF-25, DEB-10, E06/F01/US01/T02): the PDF and the CSV of an owner statement and the PDF of the F24
/// data of a month. Every file is built from what is stored (the lines and the totals of the statement, the stored withholding evidence, the stored
/// F24 record): an export never calculates an amount, never creates a withholding and never changes a row.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant.</b> The org is the caller's (the web layer resolves it); a statement or an F24 of another org is a 404, the same answer as a
/// missing one.</item>
/// <item><b>Currency.</b> Every amount of a statement is written in <see cref="OwnerStatement.Currency"/>: the export code never writes a currency of
/// its own.</item>
/// <item><b>Language.</b> The labels come from the resource files in the language the caller names (<c>it</c> or <c>en</c>).</item>
/// </list>
/// </remarks>
public interface IStatementExportService
{
    /// <summary>
    /// The PDF or the CSV of the statement. Only an Approved or Published statement is exported (422 <c>statement_not_exportable</c> for a draft);
    /// another org's statement is a 404 (<c>statement_not_found</c>).
    /// </summary>
    Task<StatementExportFile> ExportStatementAsync(
        Guid statementId,
        Guid orgId,
        StatementExportFormat format,
        string language,
        CancellationToken ct = default);

    /// <summary>
    /// The PDF of the stored F24 data of the org for the month. It never generates the record: a month without one is a 404 (<c>f24_not_found</c>).
    /// </summary>
    Task<StatementExportFile> ExportF24Async(Guid orgId, int month, int year, string language, CancellationToken ct = default);
}

/// <summary>The two formats of a statement.</summary>
public enum StatementExportFormat
{
    Pdf = 1,
    Csv = 2,
}

/// <summary>A generated file: the bytes, the media type and the name the browser saves it with.</summary>
public sealed record StatementExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// What the files of a statement print besides the statement itself: its version, the payments to the owner and the withholding stored for them
/// (E06/F05/US02). The builders print these as they are: none of them is computed while a file is rendered.
/// </summary>
/// <param name="Statement">The statement with its lines (and their bookings), its owner and its property.</param>
/// <param name="VersionNumber">The version of the statement the file is (<c>null</c> while the statement has no version).</param>
/// <param name="Payouts">The payments to the owner linked to the statement.</param>
/// <param name="Withholdings">The withholding evidence stored for those payments, with its rule version.</param>
/// <param name="Ledger">The ledger totals of that version (E06/F01/US02/T05: the statement is a versioned ledger output); <c>null</c> without one.</param>
public sealed record StatementExportData(
    OwnerStatement Statement,
    int? VersionNumber,
    IReadOnlyList<StatementPayoutEvidence> Payouts,
    IReadOnlyList<StatementWithholdingEvidence> Withholdings,
    StatementVersionTotals? Ledger = null);

/// <summary>
/// The totals of the owner ledger the current version of the statement was cut from, as stored on it: the opening and closing balance, the totals by type and
/// the SHA-256 of the lines. They are in the settlement currency of the owner, which can differ from the currency of the statement of one property.
/// </summary>
public sealed record StatementVersionTotals(
    int VersionNumber,
    string Currency,
    decimal OpeningBalance,
    decimal Revenue,
    decimal ChannelFees,
    decimal Commission,
    decimal Charges,
    decimal Payments,
    decimal Corrections,
    decimal ClosingBalance,
    string EntriesSha256);

/// <summary>A payment to the owner as a statement prints it.</summary>
/// <param name="Reference">The stable reference of the payout.</param>
/// <param name="PaidOn">The Europe/Rome day of the payment, when it is known.</param>
/// <param name="Amount">The amount paid, in the currency of the statement.</param>
/// <param name="Status">The stable code of the state of the payout.</param>
public sealed record StatementPayoutEvidence(string Reference, DateOnly? PaidOn, decimal Amount, string Status);

/// <summary>A stored withholding decision as a statement prints it.</summary>
/// <param name="PayoutReference">The payout it was decided on.</param>
/// <param name="RuleCode">The code of the rule set that decided it.</param>
/// <param name="RuleVersion">The version of that rule set.</param>
/// <param name="Decision">The stable code of the decision (<c>applicable</c>, <c>not_applicable</c>).</param>
/// <param name="GrossBase">The gross base it was computed on.</param>
/// <param name="Amount">The withholding stored (zero only for a decision that is not applicable).</param>
public sealed record StatementWithholdingEvidence(
    string PayoutReference,
    string RuleCode,
    string RuleVersion,
    string Decision,
    decimal GrossBase,
    decimal Amount);

/// <summary>The stable codes of the errors of the exports, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class StatementExportErrors
{
    /// <summary>404: the statement is not one of the caller's org (or does not exist).</summary>
    public const string StatementNotFound = "statement_not_found";

    /// <summary>422: only an approved or published statement is exported.</summary>
    public const string StatementNotExportable = "statement_not_exportable";

    /// <summary>404: the org has no stored F24 data for the month.</summary>
    public const string F24NotFound = "f24_not_found";
}
