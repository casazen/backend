using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// A request on measure made from a supplier's public showcase that waits for the customer to check the e-mail address (SP-10b).
/// It is to <see cref="ShowcaseBookingHold"/> what a message is to a slot: there is no time to hold, only the words of the
/// customer to keep until the address is checked. <b>The <see cref="ServiceRequest"/> is created only when the e-mail is
/// checked</b>: nothing a stranger types reaches the supplier before that (decision D34).
/// </summary>
/// <remarks>
/// <para><b>One life.</b> Created by <c>POST api/public/suppliers/{slug}/inquiries</c>, consumed by
/// <c>POST …/inquiries/{id}/confirm-email</c> (<see cref="ConsumedAt"/> and <see cref="ServiceRequestId"/> set, the
/// <see cref="Payload"/> erased, the token hash kept so a second click answers the same), deleted by the
/// <c>service-request-expiry</c> job after <see cref="ExpiresAt"/>.</para>
/// <para><b>Personal data.</b> Everything the customer typed, the message included, is in <see cref="Payload"/> (JSON),
/// <b>encrypted at rest</b> with the purpose of <see cref="ServiceCustomer"/> (<c>EncryptedColumns</c>); the column is
/// <c>PayloadEncrypted</c>. The only other trace of the address is <see cref="EmailHash"/>, a keyed HMAC, which is what caps the
/// requests of one address that wait.</para>
/// <para>Keyed by the supplier org and not tenant-filtered, like <see cref="ServiceCustomer"/> and
/// <see cref="ShowcaseBookingHold"/> (same reasons, same guard).</para>
/// </remarks>
[Table("ShowcaseInquiries")]
public class ShowcaseInquiry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The supplier org (<see cref="SupplierProfile.OrgId"/>).</summary>
    [Required]
    public Guid OrgId { get; set; }

    /// <summary>
    /// The id the client made up for this attempt: the same id is the same inquiry (idempotency, unique with <see cref="OrgId"/>),
    /// so a retry after a lost answer sends no second e-mail.
    /// </summary>
    public Guid ClientRequestId { get; set; }

    /// <summary>SHA-256 (lowercase hex) of the token of the e-mail link; the token itself is only in the e-mail.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 of the customer's normalized e-mail address (<see cref="ServiceCustomer.EmailHash"/>): it counts the requests
    /// of one address that wait. Deleted with the row.
    /// </summary>
    [Required, MaxLength(64)]
    public string EmailHash { get; set; } = string.Empty;

    /// <summary>
    /// What the customer typed (<see cref="ShowcaseInquiryPayload"/> as JSON): name, e-mail, phone, language, place, the words
    /// and the consent. <b>Encrypted at rest</b> (the column is <c>PayloadEncrypted</c>); <c>null</c> once the row is consumed,
    /// because the data moved to the customer, the request and its first message.
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>When the row lapses (UTC): the link no longer works.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the e-mail was checked and the request created; <c>null</c> while the row waits.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>The request born from the row when the e-mail was checked.</summary>
    public Guid? ServiceRequestId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
