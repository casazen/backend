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

    /// <summary>The supplier org (<see cref="SupplierProfile.OrgId"/>).</summary>
    [Required]
    public Guid OrgId { get; set; }

    /// <summary>
    /// The id the client made up for this booking attempt: the same id is the same hold (idempotency, unique with
    /// <see cref="OrgId"/>), so a retry after a lost answer does not take a second slot.
    /// </summary>
    public Guid ClientRequestId { get; set; }

    /// <summary>First instant of the work (UTC): the slot the customer picked.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Instant the work ends (UTC), after <see cref="StartUtc"/>: the start plus the duration of the service.</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>
    /// The code of the booking (<see cref="Services.BookingCodes"/>, 10 characters), unique for the supplier among its holds and
    /// requests. The request that is born from the hold keeps it (<see cref="ServiceRequest.PublicCode"/>).
    /// </summary>
    [Required, MaxLength(Services.BookingCodes.Length)]
    public string PublicCode { get; set; } = string.Empty;

    /// <summary>SHA-256 (lowercase hex) of the token of the e-mail link; the token itself is only in the e-mail.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 of the customer's normalized e-mail address (<see cref="ServiceCustomer.EmailHash"/>): it counts the unverified
    /// bookings of one address. Deleted with the hold.
    /// </summary>
    [Required, MaxLength(64)]
    public string EmailHash { get; set; } = string.Empty;

    /// <summary>
    /// The booking as the customer made it (<see cref="ShowcaseBookingPayload"/> as JSON): service, price, place, name, e-mail,
    /// phone, language and consent. <b>Encrypted at rest</b> (the column is <c>PayloadEncrypted</c>); <c>null</c> once the hold
    /// is consumed, because the data moved to the customer and the request.
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>When the hold lapses (UTC): the slot is free again and the link no longer works.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the e-mail was checked and the request created; <c>null</c> while the hold waits.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>The request born from the hold when the e-mail was checked.</summary>
    public Guid? ServiceRequestId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
