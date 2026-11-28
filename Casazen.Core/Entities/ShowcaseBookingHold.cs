using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// A booking made from a supplier's public showcase that waits for the customer to check the e-mail address (SP-10). It holds
/// the slot (the slot planner counts it, <c>SupplierOccupancy.Hold</c>) for <c>Suppliers:Showcase:EmailVerificationMinutes</c>
/// (30 by default) and carries the customer's data, encrypted. <b>The <see cref="ServiceRequest"/> is created only when the
/// e-mail is checked</b>: nothing a stranger types reaches the supplier before that.
/// </summary>
/// <remarks>
/// <para><b>One life.</b> Created by <c>POST api/public/suppliers/{slug}/bookings</c> under the supplier's calendar lock
/// (<c>SupplierCalendarSync</c>), after the planner confirmed the slot is free; consumed by
/// <c>POST …/bookings/{id}/confirm-email</c> (<see cref="ConsumedAt"/> and <see cref="ServiceRequestId"/> set, the
/// <see cref="Payload"/> erased, the token hash kept so a second click answers the same); deleted by the
/// <c>service-request-expiry</c> job after <see cref="ExpiresAt"/>. A consumed hold no longer holds the slot: the request does.</para>
/// <para><b>Personal data.</b> Everything the customer typed is in <see cref="Payload"/> (JSON), encrypted at rest with the
/// purpose of <see cref="ServiceCustomer"/> (<c>EncryptedColumns</c>); the column is <c>PayloadEncrypted</c>. The only other
/// trace of the address is <see cref="EmailHash"/>, a keyed HMAC, which is what limits the unverified bookings per address.</para>
/// <para>Keyed by the supplier org and not tenant-filtered, like <see cref="ServiceCustomer"/> (same reasons, same guard).</para>
/// </remarks>
[Table("ShowcaseBookingHolds")]
public class ShowcaseBookingHold
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid OrgId { get; set; }

    /// <summary>
    /// The id the client made up for this booking attempt: the same id is the same hold (idempotency, unique with
    /// <see cref="OrgId"/>), so a retry after a lost answer does not take a second slot.
    /// </summary>
    public Guid ClientRequestId { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    [Required, MaxLength(Services.BookingCodes.Length)]
    public string PublicCode { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string EmailHash { get; set; } = string.Empty;

    /// <summary>
    /// The booking as the customer made it (<see cref="ShowcaseBookingPayload"/> as JSON): service, price, place, name, e-mail,
    /// phone, language and consent. <b>Encrypted at rest</b> (the column is <c>PayloadEncrypted</c>); <c>null</c> once the hold
    /// is consumed, because the data moved to the customer and the request.
    /// </summary>
    public string? Payload { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    public Guid? ServiceRequestId { get; set; }

    /// <summary>
    /// The PaymentIntent on the platform account that blocks the amount on the customer's card (<c>capture_method = manual</c>). A hold
    /// that has one becomes a request only when <b>both</b> the e-mail is checked (<see cref="EmailVerifiedAt"/>) and the block succeeded
    /// (<see cref="PaymentAuthorizedAt"/>), in either order. A hold that lapses cancels it, so the customer's money is never kept blocked.
    /// </summary>
    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    public int? PaymentAmountCents { get; set; }

    /// <summary>When the customer's e-mail was checked, if the request is still waiting for the block; empty otherwise.</summary>
    public DateTime? EmailVerifiedAt { get; set; }

    public DateTime? PaymentAuthorizedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
