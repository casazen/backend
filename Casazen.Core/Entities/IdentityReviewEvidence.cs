using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One piece of the evidence kept for the person who reviews a self-hosted verification (E70/F01/US01/T02, NF-195, ADR-013-R20,
/// <c>Features:CheckInIdentityReviewEvidence</c>): the crop of the portrait of the document or one frame chosen by the server. <b>No image and no vector is ever
/// in a column</b>: the bytes are ciphertext in the private storage, at <see cref="StorageKey"/>; the row holds ids, a kind, a key, a content type, a hash and
/// times.
/// </summary>
/// <remarks>
/// <para><b>Time to live.</b> Every row has an expiry; the purge finds the live rows by it (<see cref="LiveExpiryIndexName"/>), deletes the object and keeps the
/// row as the trace of the deletion (<see cref="DeletedAt"/>, <see cref="DeletionReason"/>, a code of <c>IdentityEvidenceDeletionReasons</c>).</para>
/// <para><b>No foreign key</b> to the booking, the guest, the session or the log row, on purpose: a row must outlive a hard delete of any of them until the purge
/// has deleted its object, so a cascade can never leave an object nobody can find. The org is a foreign key (restrict) like every tenant table.</para>
/// </remarks>
[Table("IdentityReviewEvidences")]
public class IdentityReviewEvidence : ITenantOwned
{
    public const string LiveExpiryIndexName = "IX_IdentityReviewEvidences_ExpiresAt_Live";

    public const int StorageKeyMaxLength = 300;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public Guid? GuestId { get; set; }

    public Guid CheckInSessionId { get; set; }

    public Guid? VerificationLogId { get; set; }

    public IdentityEvidenceKind Kind { get; set; }

    [Required, MaxLength(StorageKeyMaxLength)]
    public string StorageKey { get; set; } = string.Empty;

    [Required, MaxLength(30)]
    public string ContentType { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    [MaxLength(30)]
    public string? DeletionReason { get; set; }
}
