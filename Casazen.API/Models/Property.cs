using System.ComponentModel.DataAnnotations;

namespace Casazen.API.Models;

public class Property
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid OwnerId { get; set; }
    
    [Required, MaxLength(100)]
    public string Name { get; set; }
    
    [MaxLength(2000)]
    public string Description { get; set; }
    
    [Required, MaxLength(500)]
    public string Address { get; set; }
    
    [Required, MaxLength(50)]
    public string City { get; set; }
    
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    
    public int Bedrooms { get; set; }
    public int Bathrooms { get; set; }
    public int MaxGuests { get; set; }
    
    public decimal NightlyRate { get; set; }
    public decimal CleaningFee { get; set; }
    public decimal DamageDeposit { get; set; }
    
    public List<string> Amenities { get; set; } = new();
    public List<string> PhotoUrls { get; set; } = new();
    
    [MaxLength(1000)]
    public string HouseRules { get; set; }
    
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<OtaIntegration> OtaIntegrations { get; set; } = new List<OtaIntegration>();
}