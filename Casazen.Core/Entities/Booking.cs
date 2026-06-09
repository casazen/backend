using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Bookings")]
public class Booking
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant key (AC2). Inherited from the booking's property; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("Guest")]
    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    [Required]
    public DateTime CheckInDate { get; set; }

    [Required]
    public DateTime CheckOutDate { get; set; }

    public int NumberOfGuests { get; set; }

    [Required]
    public BookingStatus Status { get; set; } = BookingStatus.Pending;

    [Required]
    public BookingSource Source { get; set; } = BookingSource.Direct;

    [MaxLength(500)]
    public string ExternalId { get; set; } = string.Empty;

    [Precision(18, 2)]
    public decimal BasePrice { get; set; }

    [Precision(18, 2)]
    public decimal TouristTax { get; set; }

    [Precision(18, 2)]
    public decimal TotalPrice { get; set; }

    /// <summary>
    /// Tourist tax amount calculated based on city rates (Italian law)
    /// </summary>
    [Precision(18, 2)]
    public decimal TouristTaxAmount { get; set; }

    /// <summary>
    /// Number of adults for tourist tax calculation (children may be exempt)
    /// </summary>
    public int NumberOfAdults { get; set; }

    /// <summary>
    /// Number of children (may be exempt from tourist tax based on age)
    /// </summary>
    public int NumberOfChildren { get; set; }

    [MaxLength(1000)]
    public string SpecialRequests { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public virtual ICollection<AlloggiatiWebReport> AlloggiatiWebReports { get; set; } = new List<AlloggiatiWebReport>();
}

public enum BookingStatus
{
    Pending,
    Confirmed,
    CheckedIn,
    CheckedOut,
    Cancelled
}

public enum BookingSource
{
    Direct,
    Airbnb,
    BookingCom,
    Expedia,
    Vrbo,
    TripAdvisor,
    Agoda,
    Local
}
