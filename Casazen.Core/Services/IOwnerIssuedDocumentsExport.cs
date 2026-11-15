using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// An owner profile of an agency, the owner in whose name documents are issued (E17/F02/US01/T01): the agency and the owner profile of that agency
/// (<c>PropertyOwner.Id</c>), as the portal grants and the agency routes name them. The documents issued in its name are the ones issued on its own issuer profile.
/// </summary>
/// <param name="OrgId">The agency.</param>
/// <param name="OwnerId">The owner profile of that agency.</param>
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

/// <summary>A file of a document issued in the name of an owner: its bytes, the name it is offered under and its content type.</summary>
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
    /// <summary>
    /// The documents issued in the name of <paramref name="owners"/>, the newest first and at most <paramref name="limit"/> of them; <paramref name="period"/> narrows them to the ones
    /// issued on a day of a month or a year. An owner with no issuer profile has none.
    /// </summary>
    Task<IReadOnlyList<OwnerIssuedDocumentInfo>> ListAsync(
        IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners,
        AccountantPackagePeriod? period,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// The PDF of one document, made from the document and the issuer as they were at the issue, in <paramref name="language"/> (Italian by default): the very PDF the agency
    /// downloads. <c>null</c> when the document is not an issued document of one of <paramref name="owners"/> (another owner's, the agency's, a draft, missing: one answer).
    /// </summary>
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

/// <summary>The stable codes of the errors of the documents issued in the name of an owner, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class OwnerIssuedDocumentsErrors
{
    /// <summary>404: the document is not an issued document of an owner profile the caller reads (another owner's, the agency's, a draft, missing: one answer).</summary>
    public const string NotFound = "owner_issued_document_not_found";

    /// <summary>The message of <see cref="NotFound"/>.</summary>
    public const string NotFoundMessageKey = "OwnerIssuedDocumentNotFound";

    /// <summary>404: there is nothing to export for the caller (the owner portal of the agency plan or the issuing of documents is not on, or the caller reads no owner profile).</summary>
    public const string NotAvailable = "owner_issued_documents_not_available";

    /// <summary>The message of <see cref="NotAvailable"/>.</summary>
    public const string NotAvailableMessageKey = "OwnerIssuedDocumentsNotAvailable";
}

/// <summary>
/// The fixed structure of the ZIP of the documents issued in the name of an owner (E17/F02/US01/T01): the folders and the name of the file, in Italian like the files the
/// accountant opens. A month has the first folder only; a year has the three.
/// </summary>
public static class OwnerIssuedDocumentsStructure
{
    /// <summary>The documents issued in the owner's name (PDF, XML when there is one) and their register, one for each issuer.</summary>
    public const string IssuedFolder = "documenti-emessi";

    /// <summary>The annual summary of those documents, and of the invoices of the agency to the owner with their recharges (a year only).</summary>
    public const string AnnualSummaryFolder = "riepilogo-annuale";

    /// <summary>The invoices of the agency to the owner issued in the year (PDF, XML when Italian) and their register (a year only).</summary>
    public const string AgencyInvoicesFolder = "fatture-agenzia";

    /// <summary>The index at the root: file, description, rows and SHA-256 of every other file.</summary>
    public const string IndexFile = AccountantPackageStructure.IndexFile;

    /// <summary>The name of the ZIP: <c>casazen-documenti-emessi-2026-10.zip</c>.</summary>
    public static string FileNameOf(AccountantPackagePeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return $"casazen-documenti-emessi-{period.Key}.zip";
    }
}
