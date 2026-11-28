using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public readonly record struct OwnerIssuedDocumentsOwner(Guid OrgId, Guid OwnerId);

/// <summary>
/// A document CasaZen issued in the name of an owner, as the owner portal lists it (E17/F02/US01/T01): the number, the day, the kind, the total, the currency, the state of the
/// SDI and whether it has an XML. Nothing of a guest: the PDF and the XML hold what the document itself says.
/// </summary>
/// <param name="DocumentId">The fiscal document.</param>
/// <param name="OrgId">The agency that issues it for the owner.</param>
/// <param name="OwnerId">The owner profile it is issued in the name of.</param>
/// <param name="Number">The number, with the series of the owner.</param>
/// <param name="IssueDate">The day it was issued (a Europe/Rome calendar day).</param>
/// <param name="Kind">An invoice, a receipt or a credit note.</param>
/// <param name="Total">The total, in <paramref name="Currency"/>.</param>
/// <param name="Currency">The currency.</param>
/// <param name="SdiStatus">One of <c>FiscalDocumentSdiStatuses</c>: where the document is with the SDI (<c>not_applicable</c> for a receipt).</param>
/// <param name="HasXml">True when the document has a FatturaPA XML to download.</param>
public sealed record OwnerIssuedDocumentInfo(
    Guid DocumentId,
    Guid OrgId,
    Guid OwnerId,
    string Number,
    DateOnly IssueDate,
    FiscalDocumentKind Kind,
    decimal Total,
    string Currency,
    string SdiStatus,
    bool HasXml);

public sealed record OwnerIssuedDocumentFile(byte[] Content, string FileName, string ContentType);

/// <summary>
/// The documents CasaZen issued in the name of an owner, for the owner portal and for the agency (E17/F02/US01/T01, NF-116, acceptance steps C8 and D2): the list, the PDF and
/// the XML of one, and the ZIP for the accountant of the owner.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The owner's documents.</b> The issued documents of a stay (<c>FiscalDocumentPurpose.GuestStay</c>, credit notes included, a draft, a discarded one and a proforma never)
/// whose issuer is the issuer profile of the owner (<c>OrgFiscalIssuerProfile.PropertyOwnerId</c>): never the agency's own documents, never the ones of another owner, never an
/// invoice of the agency to the owner. The service names the org and the owner in every query and takes no role into account: the web layer decides who may ask for which owners
/// (the portal from its grants, the agency from its permission and its scope).</item>
/// <item><b>An empty list is nothing.</b> Without owners the list is empty, a file is not found and the ZIP is refused: never every owner's.</item>
/// <item><b>Nothing personal in the log.</b> The log carries ids and counts.</item>
/// </list>
/// </remarks>
public interface IOwnerIssuedDocumentsExport
{
    Task<IReadOnlyList<OwnerIssuedDocumentInfo>> ListAsync(
        IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners,
        AccountantPackagePeriod? period,
        int limit,
        CancellationToken ct = default);

    Task<OwnerIssuedDocumentFile?> GetPdfAsync(
        IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners,
        Guid documentId,
        string? language,
        CancellationToken ct = default);

    /// <summary>
    /// The FatturaPA XML of one invoice or credit note, as it was stored at the issue. <c>null</c> as <see cref="GetPdfAsync"/>; 404 <c>fiscal_document_xml_unavailable</c> for a
    /// document of the owner that has none (a receipt).
    /// </summary>
    Task<OwnerIssuedDocumentFile?> GetXmlAsync(
        IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners,
        Guid documentId,
        CancellationToken ct = default);

    /// <summary>
    /// The ZIP for the accountant of the owner, streamed to <paramref name="destination"/>: the PDF and the XML of the documents issued in the owner's name in the period, their
    /// register (CSV and Excel: the documents, the VAT of each for each rate, the totals for each rate and nature, the stamp duty) and <c>indice.csv</c> with the SHA-256 of every file;
    /// for a year also the summary of those documents and the invoices of the agency to the owner with their recharges (register, PDF, XML and totals). Several owners (a person that reads
    /// the accounting of more than one) are one folder each. Refused before anything is written when it would hold more files than <c>DocumentArchive</c> allows (422
    /// <c>accountant_package_too_large</c>); 404 <c>owner_not_found</c> for an owner of another org.
    /// </summary>
    Task<AccountantPackageResult> BuildAsync(
        IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners,
        AccountantPackagePeriod period,
        Stream destination,
        CancellationToken ct = default);
}

public static class OwnerIssuedDocumentsErrors
{
    public const string NotFound = "owner_issued_document_not_found";

    public const string NotFoundMessageKey = "OwnerIssuedDocumentNotFound";

    public const string NotAvailable = "owner_issued_documents_not_available";

    public const string NotAvailableMessageKey = "OwnerIssuedDocumentsNotAvailable";
}

public static class OwnerIssuedDocumentsStructure
{
    public const string IssuedFolder = "documenti-emessi";

    public const string AnnualSummaryFolder = "riepilogo-annuale";

    public const string AgencyInvoicesFolder = "fatture-agenzia";

    public const string IndexFile = AccountantPackageStructure.IndexFile;

    public static string FileNameOf(AccountantPackagePeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return $"casazen-documenti-emessi-{period.Key}.zip";
    }
}
