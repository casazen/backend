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

    [MaxLength(50)]
    public string DocumentType { get; set; } = string.Empty; // "Passport", "ID Card", etc.

    [MaxLength(50)]
    public string DocumentNumber { get; set; } = string.Empty;

    public DateTime? DocumentIssueDate { get; set; }

    public DateTime? DocumentExpiryDate { get; set; }

    [MaxLength(100)]
    public string DocumentIssuingCountry { get; set; } = string.Empty;

    // GDPR Compliance
    /// <summary>
    /// Timestamp when user gave consent for data processing
    /// </summary>
    public DateTime? DataProcessingConsentDate { get; set; }

    /// <summary>
    /// Timestamp when user gave consent for marketing communications
    /// </summary>
    public DateTime? MarketingConsentDate { get; set; }

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}