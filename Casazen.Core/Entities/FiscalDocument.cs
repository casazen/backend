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
/// <item><b>Numbering.</b> A number is given at the issue, gap-free per org, issuer (<see cref="IssuerProfileId"/>: the organization or an owner who
/// issues, E17/F01/US01/T01) and <see cref="Series"/> (<see cref="SequenceNumber"/>, unique with the org, the issuer and the series when set:
/// <see cref="SeriesNumberIndexName"/>); <see cref="Number"/> is the display form, or the number Octorate gave.</item>
/// <item><b>The issuer as it was.</b> At the issue <see cref="IssuerSnapshot"/> keeps the data of the issuer the document was made with and
/// <see cref="RuleVersion"/> the version of the tax rule: the PDF and the XML of an issued document read the issuer from there, never from the profile
/// of today.</item>
/// <item><b>Privacy.</b> The recipient snapshot (<c>Recipient*</c>), the issuer snapshot, the XML and the note of a closing (<see cref="ClosedNotDueNote"/>) are <b>encrypted at rest</b> with their own purpose
/// (<c>EncryptedColumns.FiscalDocumentPurpose</c>): the columns have no length (the payload is longer than the text), the lengths are checked on input
/// (the constants below).</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the booking, never a value of a client.</item>
/// </list>
/// </remarks>
[Table("FiscalDocuments")]
public class FiscalDocument : ITenantOwned
{
    public const string SeriesNumberIndexName = "UIX_FiscalDocuments_OrgId_IssuerProfileId_Series_SequenceNumber";

    public const string OctorateDocumentIndexName = "UIX_FiscalDocuments_OrgId_OctorateDocumentId";

    public const string OwnerPeriodIndexName = "UIX_FiscalDocuments_OrgId_RecipientOwnerId_Period";

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

    public const int ClosedNotDueReasonMaxLength = 32;

    public const int ClosedNotDueNoteMaxLength = 500;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the booking (or of the Octorate connection the document was imported with).</summary>
    public Guid OrgId { get; set; }

    public Guid? BookingId { get; set; }

    public Guid? PropertyId { get; set; }

    public FiscalDocumentPurpose Purpose { get; set; } = FiscalDocumentPurpose.GuestStay;

    public Guid? SupplementForExtraOrderId { get; set; }

    /// <summary>
    /// The issuer the document is (or will be) issued on (E17/F01/US01/T01): the profile of the organization, or the profile of an owner who issues the
    /// document of the stay. It chooses the series and the counter of the number. Written with every draft CasaZen makes; the documents of before were moved to
    /// the profile of their organization by the migration <c>AddOwnerIssuerProfiles</c>. <c>null</c> for an imported document of an org without a profile.
    /// Restrict: an issued document never loses its issuer.
    /// </summary>
    public Guid? IssuerProfileId { get; set; }

    public bool ForAgencyServices { get; set; }

    /// <summary>The owner an owner invoice is made out to (E14); <c>null</c> otherwise. Restrict.</summary>
    public Guid? RecipientOwnerId { get; set; }

    /// <summary>The version of the owner statement an owner invoice was made from (E14); <c>null</c> otherwise. Restrict.</summary>
    public Guid? OwnerStatementVersionId { get; set; }

    public int? PeriodYear { get; set; }

    public int? PeriodMonth { get; set; }

    /// <summary>
    /// The first day of the service: the check-in of the stay (a calendar day). Written with the draft and kept at the issue, so the period a document
    /// states (MWSTG art. 26) never follows a later change of the booking. <c>null</c> for a document without a stay and for the older documents.
    /// </summary>
    public DateOnly? ServicePeriodFrom { get; set; }

    public DateOnly? ServicePeriodTo { get; set; }

    [MaxLength(RuleVersionMaxLength)]
    public string? RuleVersion { get; set; }

    /// <summary>
    /// The data of the issuer the document was issued with, as JSON (<c>FiscalIssuerSnapshot</c>: legal name, structured address, tax ids, Swiss VAT
    /// number, regime, country, masked IBAN), written at the issue. <c>null</c> for a draft and for the documents issued before it existed. <b>Encrypted at rest.</b>
    /// </summary>
    public string? IssuerSnapshot { get; set; }

    public FiscalPaymentChannel PaymentChannel { get; set; } = FiscalPaymentChannel.Unknown;

    public bool CreditedInFull { get; set; }

    public FiscalDocumentSource Source { get; set; } = FiscalDocumentSource.CasaZen;

    public FiscalDocumentKind Kind { get; set; } = FiscalDocumentKind.Invoice;

    public FiscalDocumentStatus Status { get; set; } = FiscalDocumentStatus.Draft;

    [Required, MaxLength(SeriesMaxLength)]
    public string Series { get; set; } = string.Empty;

    public int? SequenceNumber { get; set; }

    [MaxLength(NumberMaxLength)]
    public string? Number { get; set; }

    public DateOnly? IssueDate { get; set; }

    [Required, MaxLength(CurrencyMaxLength)]
    public string Currency { get; set; } = StayCurrency.Default;

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

    public FiscalDocumentPaymentState PaymentState { get; set; } = FiscalDocumentPaymentState.Unknown;

    /// <summary>
    /// The part of the tourist tax of the stay that its collection (E25, <see cref="StayTouristTax"/>) records as collected: what the guest paid online net of the refunds, or the whole tax once the host
    /// recorded it paid at the property (FIX/BE-PORTAL-TAXLINE). <b>Never stored</b> (no column): the readers of the guest document of a portal set it from the record of the stay
    /// (<c>FiscalDocumentService</c>) and <see cref="FiscalDocumentPortalCollection.SplitOf"/> reads it. Zero for every other document and until it is read: nothing recorded.
    /// </summary>
    [NotMapped]
    public decimal TouristTaxCollected { get; set; }

    [Required, MaxLength(SdiStatusMaxLength)]
    public string SdiStatus { get; set; } = FiscalDocumentSdiStatuses.NotApplicable;

    [MaxLength(SdiTransmissionIdMaxLength)]
    public string? SdiTransmissionId { get; set; }

    [MaxLength(SdiErrorCodeMaxLength)]
    public string? SdiErrorCode { get; set; }

    [MaxLength(SdiErrorDetailMaxLength)]
    public string? SdiErrorDetail { get; set; }

    public DateTime? SdiStatusUpdatedAt { get; set; }

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

    [MaxLength(OctorateDocumentIdMaxLength)]
    public string? OctorateDocumentId { get; set; }

    [MaxLength(OctorateReservationIdMaxLength)]
    public string? OctorateReservationId { get; set; }

    /// <summary>Octorate said it holds a PDF of the imported document (served through CasaZen, never as an Octorate address).</summary>
    public bool OctoratePdfAvailable { get; set; }

    public Guid? CreditedDocumentId { get; set; }

    public Guid? ReplacesDocumentId { get; set; }

    /// <summary>
    /// Why the replacement draft of a rejected self-billed invoice (TD17) was closed as «non più dovuta» (one of <see cref="SelfBillingCloseReasons"/>); <c>null</c> for every other document. A closed
    /// draft is <see cref="FiscalDocumentStatus.Discarded"/> (an internal outcome of CasaZen, never «scarto confermato»: the rejection is a fact of the SDI) and <b>keeps its series, its
    /// <see cref="SequenceNumber"/> and its <see cref="Number"/></b>: the unique index <see cref="SeriesNumberIndexName"/> never lets the number be taken again, and the counter is already past it. It is listed in
    /// the register of the unused numbers of the accountant package. Set together with the three columns below, in one change (a check of the table keeps them together).
    /// </summary>
    [MaxLength(ClosedNotDueReasonMaxLength)]
    public string? ClosedNotDueReason { get; set; }

    public string? ClosedNotDueNote { get; set; }

    public DateTime? ClosedNotDueAt { get; set; }

    [MaxLength(CreatedByUserIdMaxLength)]
    public string? ClosedNotDueByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(CreatedByUserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    public virtual ICollection<FiscalDocumentLine> Lines { get; set; } = new List<FiscalDocumentLine>();

    public uint RowVersion { get; set; }
}
