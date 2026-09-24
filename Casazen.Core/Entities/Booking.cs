using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Bookings")]
public class Booking : ITenantOwned
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

    /// <summary>
    /// Real arrival instant (UTC), recorded when the host registers the check-in. The Alloggiati Web term
    /// (24 hours, or 6 for short stays) runs from here; without it, from the start of the check-in day in Europe/Rome.
    /// </summary>
    public DateTime? ArrivedAt { get; set; }

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

    /// <summary>Secret token for guest self check-in link (generated when booking is confirmed).</summary>
    public Guid? CheckInToken { get; set; }

    /// <summary>UTC expiry for the check-in token (checkout + 7 days when issued).</summary>
    public DateTime? CheckInTokenExpiresAt { get; set; }

    /// <summary>Payment option selected: Immediate (pay now), OnCancellationDeadline (pay on deadline), OnSite (pay at property).</summary>
    [Required]
    public PaymentOption PaymentOption { get; set; } = PaymentOption.Immediate;

    /// <summary>Deadline for free refund (check-in - 7 days). After this date, cancellation is charged.</summary>
    public DateTime? FreeRefundDeadline { get; set; }

    /// <summary>Stripe SetupIntent ID for OnCancellationDeadline payments (to save payment method for future charge).</summary>
    [MaxLength(255)]
    public string? StripeSetupIntentId { get; set; }

    /// <summary>Stripe Payment Method ID (saved card/payment method for off-session charging on deadline).</summary>
    [MaxLength(255)]
    public string? StripePaymentMethodId { get; set; }

    /// <summary>Stripe Customer ID for off-session charges on deadline.</summary>
    [MaxLength(255)]
    public string? StripeCustomerId { get; set; }

    /// <summary>Hangfire job id for end-of-checkout-day reminder when wizard is incomplete.</summary>
    [MaxLength(100)]
    public string? CheckoutReminderJobId { get; set; }

    public DateTime? CheckoutWizardStartedAt { get; set; }

    /// <summary>
    /// Why the system cancelled the booking; null when a person cancelled it (host, admin) or it is not cancelled.
    /// Lets the payment webhook and support tell an expired checkout hold from a real cancellation (BK-21).
    /// </summary>
    public BookingCancellationReason? CancellationReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public virtual ICollection<AlloggiatiWebReport> AlloggiatiWebReports { get; set; } = new List<AlloggiatiWebReport>();

    /// <summary>Guests staying, one per line of the Alloggiati communication (CO-12), ordered by <see cref="StayGuest.Position"/>.</summary>
    public virtual ICollection<StayGuest> StayGuests { get; set; } = new List<StayGuest>();
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
    Local,

    /// <summary>
    /// Entered by the host (web console, phone or walk-in booking), as opposed to <see cref="Direct"/> which is the
    /// public checkout of the booking site. Created <see cref="BookingStatus.Confirmed"/>, it occupies its dates at
    /// once and is never touched by the expiry of abandoned checkout holds (PC-01, A2-01). Stored as 8.
    /// </summary>
    Manual
}

/// <summary>Reason of a cancellation made by the system (<see cref="Booking.CancellationReason"/>). Stored as an integer.</summary>
public enum BookingCancellationReason
{
    /// <summary>
    /// Hold of the public checkout not paid within <c>DirectBooking:PendingTtlMinutes</c>: its PaymentIntent or
    /// SetupIntent was cancelled on Stripe and the dates released (BK-21, A3-13).
    /// </summary>
    CheckoutHoldExpired = 1,

    /// <summary>
    /// The guest's payment succeeded when the dates were no longer free (the hold had expired and another booking or an
    /// iCal block took them): the booking is cancelled and the payment refunded in full automatically (BK-04, A3-04).
    /// </summary>
    DatesUnavailableAtPayment = 2,
}

public enum PaymentOption
{
    /// <summary>Pay immediately via Stripe (default, current flow).</summary>
    Immediate,

    /// <summary>Pay on the free cancellation deadline (7 days before check-in) via Stripe SetupIntent + deferred charge.</summary>
    OnCancellationDeadline,

    /// <summary>Pay on-site with no online payment (booking confirmed immediately).</summary>
    OnSite
}
