using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("Guests")]
public class Guest : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Phone, MaxLength(20)]
    public string PhoneNumber { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(50)]
    public string City { get; set; } = string.Empty;

    [MaxLength(10)]
    public string PostalCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;

    public DateTime? DateOfBirth { get; set; }

    [MaxLength(100)]
    public string PlaceOfBirth { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Nationality { get; set; } = string.Empty;

    public GuestDocumentType? DocumentType { get; set; }

    /// <summary>
    /// Identity document number. Encrypted at rest (CO-14, <c>EncryptedColumns</c>): the column is <c>text</c> because
    /// the payload is longer than the value; the length is checked on input (check-in portal, Alloggiati form).
    /// </summary>
    public string DocumentNumber { get; set; } = string.Empty;

    public DateTime? DocumentIssueDate { get; set; }

    public DateTime? DocumentExpiryDate { get; set; }

    public string DocumentIssuingCountry { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? DocumentScanUrl { get; set; }

    public DateTime? DataProcessingConsentDate { get; set; }

    [MaxLength(50)]
    public string ConsentIpAddress { get; set; } = string.Empty;

    public DateTime? DataRetentionExpiryDate { get; set; }

    public bool ErasureRequested { get; set; } = false;

    public DateTime? ErasureRequestedDate { get; set; }

    /// <summary>
    /// When the whole record was anonymized (erasure, host request or <c>Gdpr:Retention:FiscalData</c>, CO-15): nothing
    /// personal is left and the retention job no longer looks at it.
    /// </summary>
    public DateTime? DataAnonymizedDate { get; set; }

    /// <summary>
    /// When the Alloggiati data of the guest (birth, citizenship, sex, document, scan) were erased by
    /// <c>Gdpr:Retention:AlloggiatiData</c> or by an anonymization (CO-15); null while they are kept.
    /// </summary>
    public DateTime? AlloggiatiDataErasedAt { get; set; }

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public Gender? Gender { get; set; }

    public DateTime? ConsentDate { get; set; }

    [MaxLength(50)]
    public string ConsentVersion { get; set; } = string.Empty;

    public bool MarketingConsent { get; set; } = false;

    public DateTime? MarketingConsentDate { get; set; }

    [MaxLength(200)]
    public string DataProcessingPurpose { get; set; } = "Booking Management";

    public bool IsDeleted { get; set; } = false;

    public DateTime? DeletedAt { get; set; }

    [MaxLength(500)]
    public string DeletionReason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public virtual ICollection<AlloggiatiWebReport> AlloggiatiWebReports { get; set; } = new List<AlloggiatiWebReport>();

    public Guid? GuestCrmProfileId { get; set; }
    public virtual GuestCrmProfile? CrmProfile { get; set; }

    public Guest CreateSnapshot(DateTime now) => new()
    {
        OrgId = OrgId,
        FirstName = FirstName,
        LastName = LastName,
        Email = Email,
        PhoneNumber = PhoneNumber,
        Address = Address,
        City = City,
        PostalCode = PostalCode,
        Country = Country,
        DateOfBirth = DateOfBirth,
        PlaceOfBirth = PlaceOfBirth,
        Nationality = Nationality,
        DocumentType = DocumentType,
        DocumentNumber = DocumentNumber,
        DocumentIssueDate = DocumentIssueDate,
        DocumentExpiryDate = DocumentExpiryDate,
        DocumentIssuingCountry = DocumentIssuingCountry,
        DocumentScanUrl = DocumentScanUrl,
        DataProcessingConsentDate = DataProcessingConsentDate,
        ConsentIpAddress = ConsentIpAddress,
        DataRetentionExpiryDate = DataRetentionExpiryDate,
        ErasureRequested = ErasureRequested,
        ErasureRequestedDate = ErasureRequestedDate,
        DataAnonymizedDate = DataAnonymizedDate,
        AlloggiatiDataErasedAt = AlloggiatiDataErasedAt,
        Notes = Notes,
        Gender = Gender,
        ConsentDate = ConsentDate,
        ConsentVersion = ConsentVersion,
        MarketingConsent = MarketingConsent,
        MarketingConsentDate = MarketingConsentDate,
        DataProcessingPurpose = DataProcessingPurpose,
        IsDeleted = IsDeleted,
        DeletedAt = DeletedAt,
        DeletionReason = DeletionReason,
        CreatedAt = now,
        UpdatedAt = now,
    };
}

public enum GuestDocumentType
{
    Passport,
    IdentityCard,
    DriversLicense,
    Other
}

public enum Gender
{
    Male,
    Female,
    Other
}
