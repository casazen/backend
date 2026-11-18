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
    /// <summary>
    /// What a statement says, grouped as the PDF has it (the stays, the channel commissions, the withholding, the commission of the property manager, the recharges
    /// and expenses, the corrections and the payments, and the tourist tax box): the one source of the PDF, the CSV and the owner portal. 404
    /// <c>owner_statement_version_not_found</c> for a version that is not of <paramref name="orgId"/>.
    /// </summary>
    Task<OwnerStatementContent> GetContentAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default);

    /// <summary>
    /// The PDF of a version: the stored file when the version is published and has one (exactly what the owner downloads, in the language it was published in),
    /// otherwise built from the lines of the version now (a draft carries the watermark «BOZZA»). 404 <c>owner_statement_version_not_found</c>.
    /// </summary>
    Task<OwnerStatementFile> GetPdfAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default);

    /// <summary>The CSV of a version (UTF-8 with a BOM, <c>;</c>, the same lines, totals and tourist tax as the PDF). 404 <c>owner_statement_version_not_found</c>.</summary>
    Task<OwnerStatementFile> GetCsvAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default);

    /// <summary>
    /// Builds the PDF of <paramref name="version"/> from <paramref name="entries"/> (the lines it was cut from), stores it in the private bucket and stages its
    /// owner document (type statement, <c>Id</c> = the version id) and its file row in the unit of work of the caller, which saves. A version that already has its
    /// document stores nothing and answers <see cref="StoredStatementPdf.None"/>. The caller removes the file again with <see cref="DiscardStoredPdfAsync"/> when
    /// its save fails.
    /// </summary>
    Task<StoredStatementPdf> StorePublishedPdfAsync(
        OwnerStatementVersion version,
        IReadOnlyList<OwnerLedgerEntry> entries,
        string? userId,
        CancellationToken ct = default);

    /// <summary>Removes the file a failed save left in the bucket (nothing for <see cref="StoredStatementPdf.None"/>); never throws for a file that is already gone.</summary>
    Task DiscardStoredPdfAsync(StoredStatementPdf stored, CancellationToken ct = default);

    /// <summary>
    /// The stored PDF of a published version for a person of the owner portal: the download is written first (the audit of the owner documents), then the file is
    /// handed out; <c>null</c> when the version has no stored file or the agency withdrew it. The caller checks that the person reaches the version.
    /// </summary>
    Task<OwnerFileContent?> OpenPublishedPdfAsync(Guid orgId, Guid versionId, Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Queues the e-mail that tells the people of the owner portal who read the statements of the owner (the owner on its ownerships in force, the delegated contacts
    /// with <c>accounting.read</c>) that a statement was published: the agency and the link to the page «Contabilità», never an amount, a line or a guest. Called after
    /// the publication was saved. The publication is never turned into an error by it, and a notice that cannot be prepared or queued (an e-mail the queue refuses, no public
    /// address to link to, a failure reading who to tell) is never silent: it is logged with its ids and the agency gets a «To do» (once for each version) to tell the owner
    /// by hand. Nothing is raised for a portal that is off or an owner nobody reaches in it.
    /// </summary>
    Task TellOwnerPublishedAsync(OwnerStatementVersion version, CancellationToken ct = default);

    /// <summary>
    /// The PDF of an issued invoice of the property manager to an owner (E13/F01/US02/T02, a document with the purpose owner invoice that E14 makes from a statement),
    /// made from the document and the issuer as they were at the issue, in <paramref name="language"/> (Italian by default); <c>null</c> when the document is not an
    /// issued owner invoice of <paramref name="orgId"/>. The caller checks that the person reaches the owner the invoice is made out to.
    /// </summary>
    Task<OwnerStatementFile?> GetOwnerInvoicePdfAsync(Guid orgId, Guid documentId, string? language = null, CancellationToken ct = default);
}

/// <summary>A file of a statement: its bytes, the name it is offered under and its content type.</summary>
public sealed record OwnerStatementFile(byte[] Content, string FileName, string ContentType);

/// <summary>The file <see cref="IOwnerStatementDocuments.StorePublishedPdfAsync"/> put in the bucket, so a failed save can remove it again.</summary>
/// <param name="StorageKey">The key in the private bucket; <c>null</c> when nothing was stored.</param>
public sealed record StoredStatementPdf(string? StorageKey)
{
    /// <summary>Nothing was stored.</summary>
    public static StoredStatementPdf None { get; } = new((string?)null);
}

/// <summary>The part of the statement a line belongs to, in the order of the file (E13/F01/US02/T01).</summary>
public enum OwnerStatementSection
{
    /// <summary>«Soggiorni»: the revenue of the stays, the gross without the tourist tax.</summary>
    Stays = 1,

    /// <summary>«Commissioni dei canali»: what the portals kept.</summary>
    ChannelFees = 2,

    /// <summary>«Ritenuta»: the withholding kept from a payout (only when present).</summary>
    Withholding = 3,

    /// <summary>«Provvigione del PM»: the commission of the property manager under the mandate.</summary>
    Commission = 4,

    /// <summary>«Riaddebiti e spese»: the flat fees of the mandate, the card fees passed through, the charges and the expenses.</summary>
    Recharges = 5,

    /// <summary>«Rettifiche»: the manual corrections (only when present).</summary>
    Corrections = 6,

    /// <summary>The payments between the agency and the owner, shown in the «Netto» part.</summary>
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
/// <param name="Amount">The signed amount in the currency of the statement: positive is owed to the owner.</param>
/// <param name="Reversal">True when the line reverses another (<c>ReversesEntryId</c>).</param>
/// <param name="AgencyHoldsPortalAccount">True when the agency holds the portal account of the listing (V2): the commission of the channel is recharged by the property manager.</param>
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
    /// <summary>The immutable ledger evidence behind this line, including its management FX rate (not an IVA exchange rate).</summary>
    public OwnerStatementLedgerEvidence? LedgerEvidence { get; init; }
}

/// <summary>Original money, dates and source references stored by the ledger, preserved in the reconciliation appendix of the CSV.</summary>
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

/// <summary>The totals of a statement as its «Netto» part shows them: each part, the total of the period, the payments and the two balances.</summary>
/// <param name="Gross">The gross of the stays, without the tourist tax (the owner's share).</param>
/// <param name="ChannelFees">The commissions the portals kept (negative).</param>
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
    /// <summary>
    /// The totals a version stores, with the withholding as what its closing balance leaves over: a version has no column of its own for it, and every other type of ledger
    /// line is in one of its columns, so the closing balance is the opening balance plus every column plus the withholding.
    /// </summary>
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

/// <summary>What a statement says (see <see cref="IOwnerStatementDocuments.GetContentAsync"/>).</summary>
public sealed record OwnerStatementContent
{
    public required Guid VersionId { get; init; }

    public required Guid OwnerId { get; init; }

    /// <summary>The name of the agency.</summary>
    public required string AgencyName { get; init; }

    /// <summary>The name the agency keeps for the owner (never a fiscal id or an address).</summary>
    public required string OwnerName { get; init; }

    public required int Year { get; init; }

    public required int Month { get; init; }

    public required int VersionNumber { get; init; }

    public required OwnerStatementVersionStatus Status { get; init; }

    /// <summary>The currency of the statement (the settlement currency of the owner).</summary>
    public required string Currency { get; init; }

    /// <summary>The language of the labels: <c>it</c> or <c>en</c>.</summary>
    public required string Language { get; init; }

    public DateTime? PublishedAt { get; init; }

    /// <summary>The SHA-256 of the lines the version was cut from.</summary>
    public required string EntriesSha256 { get; init; }

    /// <summary>The totals, the ones the version stores (checked against its lines).</summary>
    public required OwnerStatementTotals Totals { get; init; }

    /// <summary>The lines in the order of the file: by part, then by day.</summary>
    public required IReadOnlyList<OwnerStatementLine> Lines { get; init; }

    /// <summary>The number of the invoice of the property manager to the owner for this statement, when one is linked and issued (E14).</summary>
    public string? OwnerInvoiceNumber { get; init; }

    /// <summary>The day that invoice was issued.</summary>
    public DateOnly? OwnerInvoiceDate { get; init; }

    /// <summary>The tourist tax collected by the agency for the stays of the statement, for information only.</summary>
    public required IReadOnlyList<OwnerStatementTouristTax> TouristTax { get; init; }

    /// <summary>The lines of one part.</summary>
    public IReadOnlyList<OwnerStatementLine> Of(OwnerStatementSection section) => [.. Lines.Where(line => line.Section == section)];
}

/// <summary>The stable codes of the files of the owner statements, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class OwnerStatementFileErrors
{
    /// <summary>404: the version is published but no PDF was stored with it (published before the files existed), or the agency withdrew it.</summary>
    public const string PdfNotAvailable = "owner_statement_pdf_not_available";

    public const string PdfNotAvailableMessageKey = "OwnerStatementPdfNotAvailable";

    /// <summary>404: the invoice is not one of this owner's, or has no file.</summary>
    public const string InvoiceNotFound = "owner_portal_invoice_not_found";

    public const string InvoiceNotFoundMessageKey = "OwnerPortalInvoiceNotFound";

    /// <summary>409: the document is the PDF of a published statement (its id is the id of the version): it keeps the file that was published and never gets another version.</summary>
    public const string FileImmutable = "owner_statement_file_immutable";

    public const string FileImmutableMessageKey = "OwnerStatementFileImmutable";
}
