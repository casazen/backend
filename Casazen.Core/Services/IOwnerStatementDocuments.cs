using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The files of a version of an owner statement (E13/F01/US02/T01, E13/F01/US02/T02, NF-110, NF-115, NF-111): the PDF and the CSV built from the version's own
/// lines, the published PDF stored with the version, what the owner portal reads of it, and the e-mail that tells the owner a statement was published.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>From the lines of the version.</b> Every file is made of the ledger lines the version was cut from (its <c>EntriesSha256</c> is their hash) and the
/// totals the version stores; the opening balance, the total of the period, the payments and the closing balance are checked against the version before a file
/// is made, so a file never shows a net that the version does not have (409 <c>owner_ledger_immutable</c> when the lines no longer give the stored closing
/// balance).</item>
/// <item><b>Stored once at the publication.</b> The PDF of a version is built when the version is published and stored in the private bucket as an owner document
/// of type <see cref="OwnerDocumentType.Statement"/> whose id is the id of the version (the link, with no migration), with the hash of the file: it is the file
/// the owner downloads, and it never changes. A published version without a stored file (published before the files existed) has none to hand to the owner: the
/// agency still gets a PDF built from its lines.</item>
/// <item><b>Tenant.</b> Every read names the org explicitly and ignores the tenant filter of the request: the owner portal has no org of its own. The web layer
/// decides who may ask (the agency's permission and reach, the owner's grants); a version of another org is the 404 of one that does not exist.</item>
/// <item><b>Privacy.</b> A file or a content carries no guest: stays are listed by property, dates and channel (and the booking code), never by a name, an
/// e-mail or a phone.</item>
/// </list>
/// </remarks>
public interface IOwnerStatementDocuments
{
    Task<OwnerStatementContent> GetContentAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default);

    /// <summary>
    /// The PDF of a version: the stored file when the version is published and has one (exactly what the owner downloads, in the language it was published in),
    /// otherwise built from the lines of the version now (a draft carries the watermark «BOZZA»). 404 <c>owner_statement_version_not_found</c>.
    /// </summary>
    Task<OwnerStatementFile> GetPdfAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default);

    Task<OwnerStatementFile> GetCsvAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default);

    Task<StoredStatementPdf> StorePublishedPdfAsync(
        OwnerStatementVersion version,
        IReadOnlyList<OwnerLedgerEntry> entries,
        string? userId,
        CancellationToken ct = default);

    Task DiscardStoredPdfAsync(StoredStatementPdf stored, CancellationToken ct = default);

    Task<OwnerFileContent?> OpenPublishedPdfAsync(Guid orgId, Guid versionId, Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Queues the e-mail that tells the people of the owner portal who read the statements of the owner (the owner on its ownerships in force, the delegated contacts
    /// with <c>accounting.read</c>) that a statement was published: the agency and the link to the page «Contabilità», never an amount, a line or a guest. Called after
    /// the publication was saved. The publication is never turned into an error by it, and a notice that cannot be prepared or queued (an e-mail the queue refuses, no public
    /// address to link to, a failure reading who to tell) is never silent: it is logged with its ids and the agency gets a «To do» (once for each version) to tell the owner
    /// by hand. Nothing is raised for a portal that is off or an owner nobody reaches in it.
    /// </summary>
    Task TellOwnerPublishedAsync(OwnerStatementVersion version, CancellationToken ct = default);

    Task<OwnerStatementFile?> GetOwnerInvoicePdfAsync(Guid orgId, Guid documentId, string? language = null, CancellationToken ct = default);
}

public sealed record OwnerStatementFile(byte[] Content, string FileName, string ContentType);

public sealed record StoredStatementPdf(string? StorageKey)
{
    public static StoredStatementPdf None { get; } = new((string?)null);
}

public enum OwnerStatementSection
{
    Stays = 1,

    ChannelFees = 2,

    Withholding = 3,

    Commission = 4,

    Recharges = 5,

    Corrections = 6,

    Payments = 7,
}

/// <summary>One line of a statement: a line of the owner ledger with what a reader needs to place it. No guest, never a name.</summary>
/// <param name="EntryId">The ledger line.</param>
/// <param name="Section">The part of the statement it is in.</param>
/// <param name="Type">The type of the ledger line.</param>
/// <param name="Date">The day the line counts for the owner (its recognition day).</param>
/// <param name="CheckIn">The check-in of the stay the line is about; <c>null</c> for a line that is not of a stay.</param>
/// <param name="CheckOut">The check-out of that stay.</param>
/// <param name="PropertyName">The name of the property, when the line has one.</param>
/// <param name="Channel">The channel of the stay (<see cref="BookingSource"/> name; the label of an «other portal» when it has one).</param>
/// <param name="BookingCode">The code of the booking, the reference of a stay.</param>
/// <param name="Reference">The reference of the line when it is not a stay (a payout reference, the kind of a flat fee, a month, the number of the owner invoice or of the credit note a line of VAT is of).</param>
/// <param name="Description">The words of the agency for a charge, an expense or a correction.</param>
/// <param name="SharePercent">The part of the source the owner holds.</param>
/// <param name="Amount">The signed amount in the currency of the statement: positive is owed to the owner. For a line for information only (<see cref="OwnerStatementLine.Informational"/>) the amount the ledger stores, which no balance counts.</param>
/// <param name="Reversal">True when the line reverses another (<c>ReversesEntryId</c>).</param>
/// <param name="AgencyHoldsPortalAccount">True when the agency holds the portal account of the listing (V2) and recharges the commission of the channel on its invoice (the test of the invoice): false for an account of the owner (V1) and for one of the agency that does not recharge (<see cref="OwnerStatementLine.AgencyHoldsPortalAccountWithoutRecharge"/>, <see cref="OwnerStatementLine.AgencyAccountChargedWithoutRecharge"/>).</param>
public sealed record OwnerStatementLine(
    Guid EntryId,
    OwnerStatementSection Section,
    OwnerLedgerEntryType Type,
    DateOnly Date,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    string? PropertyName,
    string? Channel,
    string? BookingCode,
    string? Reference,
    string? Description,
    decimal SharePercent,
    decimal Amount,
    bool Reversal,
    bool AgencyHoldsPortalAccount)
{
    /// <summary>
    /// True for the commission of a portal account of the agency that the organization does not recharge (V2 without the recharge): the portal bills the agency, no invoice
    /// of the property manager carries the commission, so the agency bears it and the owner is not charged (<see cref="OwnerLedgerEntryType.ChannelFeeBorneByAgency"/>,
    /// decision 1 of 20/11/2026, FIX/BE-V2-NO-RECHARGE). The statement shows it under a label of its own, for information only (<see cref="Informational"/>): never with
    /// the sentence of V1 or the one of the recharge, and in no total. Read from the line as the posting wrote it, never from the setting of today, so a statement keeps
    /// saying what its lines are. Never true together with <see cref="AgencyHoldsPortalAccount"/>.
    /// </summary>
    public bool AgencyHoldsPortalAccountWithoutRecharge => Type == OwnerLedgerEntryType.ChannelFeeBorneByAgency;

    /// <summary>
    /// True for a channel fee the owner was charged (a <see cref="OwnerLedgerEntryType.ChannelFee"/>) on a portal account of the agency that the organization does not
    /// recharge now: a line posted before FIX/BE-V2-NO-RECHARGE (the deduction it removed), or before the posting that follows a change of the setting. It stays in the
    /// totals as it was posted, so a statement cut from it reads as it was made, and it is told as what it is (the account of the agency, no recharge on an invoice), never
    /// as a commission the agency bears and never with the sentence of V1. Never true together with <see cref="AgencyHoldsPortalAccount"/>.
    /// </summary>
    public bool AgencyAccountChargedWithoutRecharge { get; init; }

    public bool Informational => !OwnerLedgerBalance.Counts(Type);

    /// <summary>The immutable ledger evidence behind this line, including its management FX rate (not an IVA exchange rate).</summary>
    public OwnerStatementLedgerEvidence? LedgerEvidence { get; init; }
}

public sealed record OwnerStatementLedgerEvidence(
    OwnerLedgerSourceType SourceType,
    Guid? SourceId,
    string? SourceReference,
    DateOnly ServiceDate,
    DateOnly RecognitionDate,
    DateOnly? PaymentDate,
    decimal OriginalAmount,
    string OriginalCurrency,
    string StatementCurrency,
    decimal FxRate,
    string? FxSource,
    DateOnly? FxDate);

/// <summary>
/// The tourist tax of one stay of the statement, collected by the agency: for information only, to be remitted to the municipality or the cantonal tourist
/// office. It is never in the net.
/// </summary>
/// <param name="PropertyName">The name of the property.</param>
/// <param name="CheckIn">The check-in of the stay.</param>
/// <param name="CheckOut">The check-out of the stay.</param>
/// <param name="Channel">The channel of the stay.</param>
/// <param name="BookingCode">The code of the booking.</param>
/// <param name="Currency">The currency of the property, which is the one the tax was collected in (it can differ from the one of the statement).</param>
/// <param name="Amount">The tax of the whole stay (never the owner's share of it).</param>
public sealed record OwnerStatementTouristTax(
    string? PropertyName,
    DateOnly CheckIn,
    DateOnly CheckOut,
    string? Channel,
    string? BookingCode,
    string Currency,
    decimal Amount);

/// <summary>
/// The totals of a statement as its «Netto» part shows them: each part, the total of the period, the payments and the two balances. A line for information only
/// (<see cref="OwnerStatementLine.Informational"/>: the commission of a portal the agency bears) is in none of them.
/// </summary>
/// <param name="Gross">The gross of the stays, without the tourist tax (the owner's share).</param>
/// <param name="ChannelFees">The commissions the portals kept that the owner bears (negative): never the ones the agency bears.</param>
/// <param name="Withholding">The withholding kept from the payouts (negative).</param>
/// <param name="Commission">The commission of the property manager (negative).</param>
/// <param name="Recharges">The recharges and the expenses (negative).</param>
/// <param name="Corrections">The manual corrections.</param>
/// <param name="Period">The total of the period: the five parts above and the corrections.</param>
/// <param name="Payments">The payments between the agency and the owner (a payment to the owner is negative).</param>
/// <param name="OpeningBalance">The balance of the months before.</param>
/// <param name="ClosingBalance">What the agency owes the owner at the end of the month: the opening balance, the total of the period and the payments.</param>
public sealed record OwnerStatementTotals(
    decimal Gross,
    decimal ChannelFees,
    decimal Withholding,
    decimal Commission,
    decimal Recharges,
    decimal Corrections,
    decimal Period,
    decimal Payments,
    decimal OpeningBalance,
    decimal ClosingBalance)
{
    public static OwnerStatementTotals Of(OwnerStatementVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var withholding = version.ClosingBalance - version.OpeningBalance
            - (version.Revenue + version.ChannelFees + version.Commission + version.Charges + version.Corrections + version.Payments);
        var period = version.Revenue + version.ChannelFees + withholding + version.Commission + version.Charges + version.Corrections;
        return new OwnerStatementTotals(
            version.Revenue,
            version.ChannelFees,
            withholding,
            version.Commission,
            version.Charges,
            version.Corrections,
            period,
            version.Payments,
            version.OpeningBalance,
            version.ClosingBalance);
    }
}

public sealed record OwnerStatementContent
{
    public required Guid VersionId { get; init; }

    public required Guid OwnerId { get; init; }

    public required string AgencyName { get; init; }

    public required string OwnerName { get; init; }

    public required int Year { get; init; }

    public required int Month { get; init; }

    public required int VersionNumber { get; init; }

    public required OwnerStatementVersionStatus Status { get; init; }

    public required string Currency { get; init; }

    public required string Language { get; init; }

    public DateTime? PublishedAt { get; init; }

    public required string EntriesSha256 { get; init; }

    public required OwnerStatementTotals Totals { get; init; }

    public required IReadOnlyList<OwnerStatementLine> Lines { get; init; }

    public string? OwnerInvoiceNumber { get; init; }

    public DateOnly? OwnerInvoiceDate { get; init; }

    public required IReadOnlyList<OwnerStatementTouristTax> TouristTax { get; init; }

    public IReadOnlyList<OwnerStatementLine> Of(OwnerStatementSection section) => [.. Lines.Where(line => line.Section == section)];
}

public static class OwnerStatementFileErrors
{
    public const string PdfNotAvailable = "owner_statement_pdf_not_available";

    public const string PdfNotAvailableMessageKey = "OwnerStatementPdfNotAvailable";

    public const string InvoiceNotFound = "owner_portal_invoice_not_found";

    public const string InvoiceNotFoundMessageKey = "OwnerPortalInvoiceNotFound";

    public const string FileImmutable = "owner_statement_file_immutable";

    public const string FileImmutableMessageKey = "OwnerStatementFileImmutable";
}
