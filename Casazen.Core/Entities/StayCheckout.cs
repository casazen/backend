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

    /// <summary>Tenant of the row, copied from the booking (TN-2).</summary>
    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Booking))]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>Step the host is on (saved progress): the wizard opens here again.</summary>
    public CheckoutWizardStep CurrentStep { get; set; } = CheckoutWizardStep.StaySummary;

    /// <summary>Step 1: the host confirmed that the guest left (saved progress; required again to complete).</summary>
    public bool DepartureConfirmed { get; set; }

    /// <summary>Step 3: cleaning request to a supplier or skipped; null until the host chooses.</summary>
    public CheckoutCleaningChoice? CleaningChoice { get; set; }

    /// <summary>Step 3: supplier chosen for the cleaning request (only with <see cref="CheckoutCleaningChoice.Request"/>).</summary>
    public Guid? CleaningSupplierOrgId { get; set; }

    /// <summary>Step 3: category code of the request (<c>ServiceCategories</c>, cleaning by default).</summary>
    [MaxLength(100)]
    public string? CleaningCategory { get; set; }

    /// <summary>Step 3: notes for the supplier.</summary>
    [MaxLength(1000)]
    public string? CleaningNotes { get; set; }

    /// <summary>
    /// The supplier request created with the check-out (tied to the stay, SU-07); null when the host skipped the cleaning
    /// or closed the stay without choosing.
    /// </summary>
    public Guid? CleaningRequestId { get; set; }
    public virtual ServiceRequest? CleaningRequest { get; set; }

    /// <summary>Step 4: how the tourist tax of the stay was collected, as declared by the host; null = not declared.</summary>
    public TouristTaxCollection? TouristTaxCollection { get; set; }

    /// <summary>
    /// Step 5 while the wizard is open: the answer of the host (ready or not yet). Once the stay is checked out the
    /// property is ready only when <see cref="PropertyReadyAt"/> is set.
    /// </summary>
    public bool? PropertyReady { get; set; }

    /// <summary>Step 5: notes of the host on the state of the property (damages, missing items, ...).</summary>
    [MaxLength(1000)]
    public string? PropertyNotes { get; set; }

    /// <summary>When the stay was checked out (UTC); null while the wizard is open.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>When the host declared the property ready for the next guest (UTC), at the check-out or later.</summary>
    public DateTime? PropertyReadyAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The 5 steps of the check-out wizard, in order (stored as an integer).</summary>
public enum CheckoutWizardStep
{
    /// <summary>Summary of the stay and confirmation that the guest left.</summary>
    StaySummary = 1,

    /// <summary>Alloggiati Web communication of the stay: sent, or still to send (CO-11).</summary>
    Alloggiati = 2,

    /// <summary>Cleaning request to a supplier of the property's comune, or skipped.</summary>
    Cleaning = 3,

    /// <summary>Tourist tax of the stay (BK-03) and how it was collected.</summary>
    TouristTax = 4,

    /// <summary>The property is ready for the next guest, with notes.</summary>
    PropertyReady = 5,
}

/// <summary>What the host chose for the cleaning at check-out (stored as an integer).</summary>
public enum CheckoutCleaningChoice
{
    /// <summary>A request to the chosen supplier, created with the check-out.</summary>
    Request = 1,

    /// <summary>No request (the host cleans, or already asked a supplier).</summary>
    Skip = 2,
}

/// <summary>How the tourist tax of a stay was collected, as declared by the host at check-out (stored as an integer).</summary>
public enum TouristTaxCollection
{
    /// <summary>Collected online, with the payment of the booking (booking site or channel).</summary>
    CollectedOnline = 1,

    /// <summary>Collected at the property.</summary>
    CollectedAtProperty = 2,

    /// <summary>Due but not collected from the guest.</summary>
    NotCollected = 3,

    /// <summary>Not due for this stay (exempt guests, or no tax in the comune).</summary>
    NotDue = 4,
}
