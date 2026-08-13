using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("Guests")]
public class Guest
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

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

    // Alloggiati Web - Required fields for Italian police reporting
    public DateTime? DateOfBirth { get; set; }

    [MaxLength(100)]
    public string PlaceOfBirth { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Nationality { get; set; } = string.Empty;

    public GuestDocumentType? DocumentType { get; set; }

    [MaxLength(50)]
    public string DocumentNumber { get; set; } = string.Empty;

    public DateTime? DocumentIssueDate { get; set; }

    public DateTime? DocumentExpiryDate { get; set; }

    [MaxLength(100)]
    public string DocumentIssuingCountry { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? DocumentScanUrl { get; set; }

    // GDPR Compliance
    /// <summary>
    /// Timestamp when user gave consent for data processing
    /// </summary>
    public DateTime? DataProcessingConsentDate { get; set; }

    /// <summary>
    /// IP address from which consent was given
    /// </summary>
    [MaxLength(50)]
    public string ConsentIpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Date when data should be automatically deleted (GDPR retention policy)
    /// </summary>
    public DateTime? DataRetentionExpiryDate { get; set; }

    /// <summary>
    /// Flag indicating user requested right to erasure (GDPR Article 17)
    /// </summary>
    public bool ErasureRequested { get; set; } = false;

    /// <summary>
    /// Date when erasure was requested
    /// </summary>
    public DateTime? ErasureRequestedDate { get; set; }

    /// <summary>
    /// Date when data was anonymized/deleted
    /// </summary>
    public DateTime? DataAnonymizedDate { get; set; }

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public Gender? Gender { get; set; }

    // GDPR Compliance Fields (GDPR Articles 6, 7, 13-17)
    public DateTime? ConsentDate { get; set; }

    [MaxLength(50)]
    public string ConsentVersion { get; set; } = string.Empty;

    public bool MarketingConsent { get; set; } = false;

    public DateTime? MarketingConsentDate { get; set; }

    public DateTime DataRetentionUntil { get; set; } = DateTime.UtcNow.AddYears(7);

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

    public Guest CreateSnapshot(DateTime now) => new()
    {
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
        Notes = Notes,
        Gender = Gender,
        ConsentDate = ConsentDate,
        ConsentVersion = ConsentVersion,
        MarketingConsent = MarketingConsent,
        MarketingConsentDate = MarketingConsentDate,
        DataRetentionUntil = DataRetentionUntil,
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
