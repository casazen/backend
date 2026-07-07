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

public enum PaymentOption
{
    /// <summary>Pay immediately via Stripe (default, current flow).</summary>
    Immediate,

    /// <summary>Pay on the free cancellation deadline (7 days before check-in) via Stripe SetupIntent + deferred charge.</summary>
    OnCancellationDeadline,

    /// <summary>Pay on-site with no online payment (booking confirmed immediately).</summary>
    OnSite
}
