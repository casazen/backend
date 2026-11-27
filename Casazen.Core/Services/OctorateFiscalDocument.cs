namespace Casazen.Core.Services;

/// <summary>
/// A document Octorate issued for an accommodation (FIS-04, NF-84, E06/F03/US02/T01), as the list of its documents gives it
/// (<c>GET /rest/v2/invoice/{acc}</c>). Every member Octorate does not state is <c>null</c>, never a default: an amount that is missing stays missing.
/// </summary>
/// <param name="DocumentId">The id of the document on Octorate.</param>
/// <param name="ReservationId">The reservation it is about (<c>refer</c>), when it names one.</param>
/// <param name="Type">What kind of document it is.</param>
/// <param name="Number">The number Octorate gave.</param>
/// <param name="Date">The day of the document.</param>
/// <param name="TaxableBase">The taxable amount.</param>
/// <param name="Vat">The VAT.</param>
/// <param name="Total">The total.</param>
/// <param name="Currency">The ISO 4217 code of the amounts.</param>
/// <param name="Paid">Whether it was paid.</param>
/// <param name="SdiGroup">The state on the SDI (<c>fiscalInvoiceGroup</c>: <c>EMPTY</c>, <c>REJECTED_BEFORE</c>, <c>BEFORE_SDI</c>, <c>SDI_PROCESSING</c>,
/// <c>SDI_REJECTED</c>, <c>DONE</c>); present only when the treasury integration of the structure is active.</param>
/// <param name="PdfAvailable">Octorate says it holds a PDF of the document.</param>
/// <param name="RecipientName">The recipient as the document shows it.</param>
public sealed record OctorateFiscalDocument(
    string DocumentId,
    string? ReservationId,
    OctorateFiscalDocumentType Type,
    string? Number,
    DateOnly? Date,
    decimal? TaxableBase,
    decimal? Vat,
    decimal? Total,
    string? Currency,
    bool? Paid,
    string? SdiGroup,
    bool PdfAvailable,
    string? RecipientName);

/// <summary>The eight types of the documents of Octorate (S24), and the ones CasaZen does not know.</summary>
public enum OctorateFiscalDocumentType
{
    Unknown = 0,
    Invoice = 1,
    CorrectingInvoice = 2,
    SimplifiedInvoice = 3,
    FiscalReceipt = 4,
    GenericReceipt = 5,
    CreditNote = 6,
    Proforma = 7,

    /// <summary>A summary that is not a fiscal document: never imported.</summary>
    SummaryDocument = 8,
}
