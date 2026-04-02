using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Validation;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Properties")]
public class Property
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string OwnerId { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string City { get; set; } = string.Empty;

    [MaxLength(10)]
    public string PostalCode { get; set; } = string.Empty;

    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }

    public int Bedrooms { get; set; }
    public int Bathrooms { get; set; }
    public int MaxGuests { get; set; }

    [Precision(18, 2)]
    public decimal NightlyRate { get; set; }

    [Precision(18, 2)]
    public decimal CleaningFee { get; set; }

    [Precision(18, 2)]
    public decimal DamageDeposit { get; set; }

    public List<string> Amenities { get; set; } = new();
    public List<string> PhotoUrls { get; set; } = new();

    [MaxLength(1000)]
    public string HouseRules { get; set; } = string.Empty;

    // Italian regulatory compliance - D.L. 145/2023
    [MaxLength(25)]
    [CinCode]
    public string? CinCode { get; set; } // Format: IT-XXXXX-XXXXXXXXXX

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public virtual ICollection<OtaIntegration> OtaIntegrations { get; set; } = new List<OtaIntegration>();
}