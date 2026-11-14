using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A fiscal document (FIS-03, FIS-04, NF-83, NF-84, NF-112, NF-113, ADR-019, E06/F01/US02/T01, E11/F01/US01/T01): one CasaZen issued (for a booking of
/// any source, or to an owner), or one Octorate issued and CasaZen imported read-only. A draft has no number; an issued document is immutable (its
/// amounts, recipient, issuer and lines never change again) and is kept for the legal retention of ten years (ADR-033-R7): no cascade reaches this table,
/// neither the removal of a booking nor the erasure of a guest.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Numbering.</b> A number is given at the issue, gap-free per org and <see cref="Series"/> (<see cref="SequenceNumber"/>, unique with the
/// org and the series when set: <see cref="SeriesNumberIndexName"/>); <see cref="Number"/> is the display form, or the number Octorate gave.</item>
/// <item><b>The issuer as it was.</b> At the issue <see cref="IssuerSnapshot"/> keeps the data of the issuer the document was made with and
/// <see cref="RuleVersion"/> the version of the tax rule: the PDF and the XML of an issued document read the issuer from there, never from the profile
/// of today.</item>
/// <item><b>Privacy.</b> The recipient snapshot (<c>Recipient*</c>), the issuer snapshot and the XML are <b>encrypted at rest</b> with their own purpose
/// (<c>EncryptedColumns.FiscalDocumentPurpose</c>): the columns have no length (the payload is longer than the text), the lengths are checked on input
/// (the constants below).</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the booking, never a value of a client.</item>
/// </list>
/// </remarks>
[Table("FiscalDocuments")]
public class FiscalDocument : ITenantOwned
{
    /// <summary>Unique (<see cref="OrgId"/>, <see cref="Series"/>, <see cref="SequenceNumber"/>) where the number is set: no number twice.</summary>
    public const string SeriesNumberIndexName = "UIX_FiscalDocuments_OrgId_Series_SequenceNumber";

    /// <summary>Unique (<see cref="OrgId"/>, <see cref="OctorateDocumentId"/>) where set: an Octorate document is imported once.</summary>
    public const string OctorateDocumentIndexName = "UIX_FiscalDocuments_OrgId_OctorateDocumentId";

    /// <summary>
    /// Unique (<see cref="OrgId"/>, <see cref="RecipientOwnerId"/>, <see cref="PeriodYear"/>, <see cref="PeriodMonth"/>) for the owner invoices that are
    /// neither discarded, nor credit notes, nor credited in full (<see cref="CreditedInFull"/>): one live invoice per owner and period (E14).
    /// </summary>
    public const string OwnerPeriodIndexName = "UIX_FiscalDocuments_OrgId_RecipientOwnerId_Period";

    /// <summary>The longest <see cref="RuleVersion"/>.</summary>
    public const int RuleVersionMaxLength = 64;

    public const int SeriesMaxLength = 20;
    public const int NumberMaxLength = 50;
    public const int CurrencyMaxLength = 3;
    public const int RecipientNameMaxLength = 200;
    public const int RecipientFiscalCodeMaxLength = 16;
    public const int RecipientVatNumberMaxLength = 28;
    public const int RecipientAddressStreetMaxLength = 200;
    public const int RecipientAddressCivicMaxLength = 20;
    public const int RecipientPostalCodeMaxLength = 10;
    public const int RecipientCityMaxLength = 100;
    public const int RecipientProvinceMaxLength = 2;
    public const int RecipientCountryCodeMaxLength = 2;
    public const int RecipientSdiCodeMaxLength = 7;
    public const int RecipientPecMaxLength = 254;
    public const int VatNatureCodeMaxLength = 4;
    public const int SdiStatusMaxLength = 32;
    public const int SdiTransmissionIdMaxLength = 255;
    public const int SdiErrorCodeMaxLength = 20;
    public const int SdiErrorDetailMaxLength = 500;
    public const int OctorateDocumentIdMaxLength = 100;
    public const int OctorateAccommodationIdMaxLength = 100;
    public const int OctorateReservationIdMaxLength = 50;
    public const int CreatedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the booking (or of the Octorate connection the document was imported with).</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// The booking the document is of; <c>null</c> for an imported document no booking matches and for a document that is not of a stay (an owner
    /// invoice). Never removed with the booking (restrict).
    /// </summary>
    public Guid? BookingId { get; set; }

    /// <summary>The property the document is about (the property of the booking); <c>null</c> when it is about none. Restrict.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>What the document is for: the stay of a guest (the default), an invoice to an owner, a self-billed invoice.</summary>
    public FiscalDocumentPurpose Purpose { get; set; } = FiscalDocumentPurpose.GuestStay;

    /// <summary>The owner an owner invoice is made out to (E14); <c>null</c> otherwise. Restrict.</summary>
    public Guid? RecipientOwnerId { get; set; }

    /// <summary>The version of the owner statement an owner invoice was made from (E14); <c>null</c> otherwise. Restrict.</summary>
    public Guid? OwnerStatementVersionId { get; set; }

    /// <summary>The year of the period an owner invoice is about (with <see cref="PeriodMonth"/>, the key of <see cref="OwnerPeriodIndexName"/>).</summary>
    public int? PeriodYear { get; set; }

    /// <summary>The month (1 to 12) of the period an owner invoice is about.</summary>
    public int? PeriodMonth { get; set; }

    /// <summary>
    /// The first day of the service: the check-in of the stay (a calendar day). Written with the draft and kept at the issue, so the period a document
    /// states (MWSTG art. 26) never follows a later change of the booking. <c>null</c> for a document without a stay and for the older documents.
    /// </summary>
    public DateOnly? ServicePeriodFrom { get; set; }

    /// <summary>The last day of the service: the check-out of the stay (a calendar day). See <see cref="ServicePeriodFrom"/>.</summary>
    public DateOnly? ServicePeriodTo { get; set; }

    /// <summary>
    /// The version of the tax rule the document was made with (<c>FiscalDocuments:TaxRules</c>, chosen by the date of the stay); written for every
    /// document CasaZen issues. <c>null</c> for an imported document and for the documents issued before the rules had versions.
    /// </summary>
    [MaxLength(RuleVersionMaxLength)]
    public string? RuleVersion { get; set; }

    /// <summary>
    /// The data of the issuer the document was issued with, as JSON (<c>FiscalIssuerSnapshot</c>: legal name, structured address, tax ids, Swiss VAT
    /// number, regime, country, masked IBAN), written at the issue. <c>null</c> for a draft and for the documents issued before it existed. <b>Encrypted at rest.</b>
    /// </summary>
    public string? IssuerSnapshot { get; set; }

    /// <summary>Who collected the money of the document (E12); <see cref="FiscalPaymentChannel.Unknown"/> until it is known.</summary>
    public FiscalPaymentChannel PaymentChannel { get; set; } = FiscalPaymentChannel.Unknown;

    /// <summary>
    /// True once credit notes credited the whole total of the document (set in the transaction of the credit note that completes it): an owner invoice
    /// credited in full no longer holds its owner and period (<see cref="OwnerPeriodIndexName"/>).
    /// </summary>
    public bool CreditedInFull { get; set; }

    public FiscalDocumentSource Source { get; set; } = FiscalDocumentSource.CasaZen;

    public FiscalDocumentKind Kind { get; set; } = FiscalDocumentKind.Invoice;

    public FiscalDocumentStatus Status { get; set; } = FiscalDocumentStatus.Draft;

    /// <summary>The series the document is numbered in (empty for an imported document that has none).</summary>
    [Required, MaxLength(SeriesMaxLength)]
    public string Series { get; set; } = string.Empty;

    /// <summary>The number within the series, set at the issue; <c>null</c> for a draft and for an imported document.</summary>
    public int? SequenceNumber { get; set; }

    /// <summary>The display number (<c>{series}/{number}</c>), or the number Octorate gave.</summary>
    [MaxLength(NumberMaxLength)]
    public string? Number { get; set; }

    /// <summary>The day of the issue, a Europe/Rome calendar day; <c>null</c> for a draft.</summary>
    public DateOnly? IssueDate { get; set; }

    /// <summary>ISO 4217 code of every amount of the document: the currency of the property (<see cref="StayCurrency"/>).</summary>
    [Required, MaxLength(CurrencyMaxLength)]
    public string Currency { get; set; } = StayCurrency.Default;

    // ---------------------------------------------------------------- recipient snapshot (encrypted at rest)

    /// <summary>A private person or a company; <c>null</c> when an imported document does not say.</summary>
    public InvoiceRecipientKind? RecipientKind { get; set; }

    public string? RecipientName { get; set; }

    public string? RecipientFiscalCode { get; set; }

    public string? RecipientVatNumber { get; set; }

    public string? RecipientAddressStreet { get; set; }

    public string? RecipientAddressCivic { get; set; }

    public string? RecipientPostalCode { get; set; }

    public string? RecipientCity { get; set; }

    public string? RecipientProvince { get; set; }

    public string? RecipientCountryCode { get; set; }

    public string? RecipientSdiCode { get; set; }

    public string? RecipientPec { get; set; }

    // ---------------------------------------------------------------- amounts (as computed by FiscalDocumentTotals, or as Octorate gave them)

    [Precision(18, 2)]
    public decimal TaxableBase { get; set; }

    [Precision(18, 2)]
    public decimal VatAmount { get; set; }

    [Precision(18, 2)]
    public decimal StampDuty { get; set; }

    [Precision(18, 2)]
    public decimal Total { get; set; }

    /// <summary>The VAT rate of the document when it has one rate; <c>null</c> when it has several or none is known.</summary>
    [Precision(5, 2)]
    public decimal? VatRatePercent { get; set; }

    /// <summary>The nature code of an operation without VAT (<c>N2.2</c>...), when the rate is zero.</summary>
    [MaxLength(VatNatureCodeMaxLength)]
    public string? VatNatureCode { get; set; }

    // ---------------------------------------------------------------- states

    public FiscalDocumentPaymentState PaymentState { get; set; } = FiscalDocumentPaymentState.Unknown;

    /// <summary>One of <see cref="FiscalDocumentSdiStatuses"/>.</summary>
    [Required, MaxLength(SdiStatusMaxLength)]
    public string SdiStatus { get; set; } = FiscalDocumentSdiStatuses.NotApplicable;

    [MaxLength(SdiTransmissionIdMaxLength)]
    public string? SdiTransmissionId { get; set; }

    [MaxLength(SdiErrorCodeMaxLength)]
    public string? SdiErrorCode { get; set; }

    [MaxLength(SdiErrorDetailMaxLength)]
    public string? SdiErrorDetail { get; set; }

    public DateTime? SdiStatusUpdatedAt { get; set; }

    // ---------------------------------------------------------------- content and links

    /// <summary>The FatturaPA XML of a CasaZen document, validated before it was stored. <b>Encrypted at rest.</b></summary>
    public string? FatturaPaXml { get; set; }

    /// <summary>
    /// The accommodation of Octorate an imported document was read from (the import reads one accommodation at a time): where its PDF is asked, also for a
    /// document that belongs to no booking, and the date of the next read of that accommodation (<c>MAX(UpdatedAt)</c>), so a structure that failed is read
    /// again from where it stopped whatever the others did. <c>null</c> for a document of CasaZen and for one imported before this column existed (the next
    /// read fills it).
    /// </summary>
    [MaxLength(OctorateAccommodationIdMaxLength)]
    public string? OctorateAccommodationId { get; set; }

    /// <summary>The id of an imported document on Octorate (unique within the org: <see cref="OctorateDocumentIndexName"/>).</summary>
    [MaxLength(OctorateDocumentIdMaxLength)]
    public string? OctorateDocumentId { get; set; }

    /// <summary>The reservation of Octorate an imported document names.</summary>
    [MaxLength(OctorateReservationIdMaxLength)]
    public string? OctorateReservationId { get; set; }

    /// <summary>Octorate said it holds a PDF of the imported document (served through CasaZen, never as an Octorate address).</summary>
    public bool OctoratePdfAvailable { get; set; }

    /// <summary>The document a credit note credits (restrict: an issued document is never removed).</summary>
    public Guid? CreditedDocumentId { get; set; }

    /// <summary>The rejected document a correction replaces (restrict).</summary>
    public Guid? ReplacesDocumentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The person who created the document; <c>null</c> for an imported one.</summary>
    [MaxLength(CreatedByUserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    /// <summary>The lines of a CasaZen document, in order (an imported document has none).</summary>
    public virtual ICollection<FiscalDocumentLine> Lines { get; set; } = new List<FiscalDocumentLine>();

    /// <summary>The PostgreSQL <c>xmin</c> of the row: two writers that read the same draft cannot both change it.</summary>
    public uint RowVersion { get; set; }
}
