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

    /// <summary>
    /// "Pay at the property" requests only (<see cref="PaymentOption.OnSite"/>, decision D5, BK-06): when the guest
    /// confirmed the email address through the link of the "request received" email. Until then the request is not
    /// sent to the host and holds its dates only for <c>DirectBooking:OnSiteEmailVerificationMinutes</c>.
    /// </summary>
    public DateTime? GuestEmailVerifiedAt { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the email confirmation token of a "pay at the property" request; the raw token is only in the
    /// link sent to the guest.
    /// </summary>
    [MaxLength(64)]
    public string? GuestEmailVerificationTokenHash { get; set; }

    /// <summary>
    /// "Pay at the property" requests only: until when the pending request holds its dates. First the end of the email
    /// confirmation window, then, once the email is confirmed, the host's answer deadline
    /// (<c>DirectBooking:OnSiteApprovalHours</c>). Past it the <c>checkout-hold-expiry</c> job cancels the request.
    /// </summary>
    public DateTime? RequestExpiresAt { get; set; }

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

    /// <summary>"Pay at the property" request declined by the host (BK-06, D5).</summary>
    OnSiteRequestDeclined = 2,

    /// <summary>"Pay at the property" request not answered by the host within <c>DirectBooking:OnSiteApprovalHours</c> (BK-06).</summary>
    OnSiteRequestExpired = 3,

    /// <summary>
    /// "Pay at the property" request whose guest did not confirm the email address within
    /// <c>DirectBooking:OnSiteEmailVerificationMinutes</c>: never sent to the host (BK-06, A3-06).
    /// </summary>
    OnSiteEmailNotConfirmed = 4,
}

public enum PaymentOption
{
    /// <summary>Pay immediately via Stripe (default, current flow).</summary>
    Immediate,

    /// <summary>Pay on the free cancellation deadline (7 days before check-in) via Stripe SetupIntent + deferred charge.</summary>
    OnCancellationDeadline,

    /// <summary>
    /// Pay at the property, no online payment. Never confirmed at once (decision D5, BK-06): the booking stays
    /// <see cref="BookingStatus.Pending"/> as a request that the guest confirms by email and the host accepts or
    /// declines (<see cref="Services.OnSiteRequests"/>).
    /// </summary>
    OnSite
}
