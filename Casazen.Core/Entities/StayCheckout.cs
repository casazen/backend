using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Check-out of one stay (CO-17, A5-24): the progress of the 5-step check-out wizard while the host goes through it,
/// then what the host declared when closing the stay (cleaning request or skip, tourist tax collected, property ready).
/// One row per booking (<see cref="BookingId"/> is unique), created when the wizard starts or when the stay is checked
/// out without the wizard (<c>POST /api/bookings/{id}/check-out</c>). Every write happens under the lock of the booking
/// (BK-02, CO-08), so the wizard of the web and of the app never overwrite each other half way.
/// </summary>
/// <remarks>
/// The compliance cockpit reads it for the turnovers still open: a stay checked out (<see cref="CompletedAt"/>) whose
/// property was not declared ready (<see cref="PropertyReadyAt"/> null).
/// </remarks>
[Table("StayCheckouts")]
[Index(nameof(BookingId), IsUnique = true)]
public class StayCheckout : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Booking))]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public CheckoutWizardStep CurrentStep { get; set; } = CheckoutWizardStep.StaySummary;

    public bool DepartureConfirmed { get; set; }

    public CheckoutCleaningChoice? CleaningChoice { get; set; }

    public Guid? CleaningSupplierOrgId { get; set; }

    [MaxLength(100)]
    public string? CleaningCategory { get; set; }

    [MaxLength(1000)]
    public string? CleaningNotes { get; set; }

    public Guid? CleaningRequestId { get; set; }
    public virtual ServiceRequest? CleaningRequest { get; set; }

    public TouristTaxCollection? TouristTaxCollection { get; set; }

    public bool? PropertyReady { get; set; }

    [MaxLength(1000)]
    public string? PropertyNotes { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? PropertyReadyAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum CheckoutWizardStep
{
    StaySummary = 1,

    /// <summary>Alloggiati Web communication of the stay: sent, or still to send (CO-11).</summary>
    Alloggiati = 2,

    Cleaning = 3,

    TouristTax = 4,

    PropertyReady = 5,
}

public enum CheckoutCleaningChoice
{
    Request = 1,

    Skip = 2,
}

public enum TouristTaxCollection
{
    CollectedOnline = 1,

    CollectedAtProperty = 2,

    NotCollected = 3,

    NotDue = 4,
}
