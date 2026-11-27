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
    /// <summary>Name of the index of the purge: <see cref="ExpiresAt"/> of the rows not deleted yet.</summary>
    public const string LiveExpiryIndexName = "IX_IdentityReviewEvidences_ExpiresAt_Live";

    /// <summary>Longest key of the object in the private storage.</summary>
    public const int StorageKeyMaxLength = 300;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The booking of the verification.</summary>
    public Guid BookingId { get; set; }

    /// <summary>The guest the verification was bound to, when known (<c>GuestCheckInSession.IdentityGuestId</c>).</summary>
    public Guid? GuestId { get; set; }

    /// <summary>The check-in session of the verification.</summary>
    public Guid CheckInSessionId { get; set; }

    /// <summary>The row of <c>BiometricVerificationLogs</c> of the attempt, once it is written.</summary>
    public Guid? VerificationLogId { get; set; }

    /// <summary>What the object is: the crop of the portrait or a frame.</summary>
    public IdentityEvidenceKind Kind { get; set; }

    /// <summary>The key of the encrypted object in the private storage.</summary>
    [Required, MaxLength(StorageKeyMaxLength)]
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>The content type of the picture once decrypted (<c>image/jpeg</c>).</summary>
    [Required, MaxLength(30)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The SHA-256 (hex) of the stored object, to verify it when it is read.</summary>
    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the object must be deleted at the latest (UTC): <see cref="CreatedAt"/> plus <c>IdentityVerification:Evidence:RetentionHours</c>.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the object was deleted (UTC); null while it is kept.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Why it was deleted: <c>decided</c>, <c>expired</c>, <c>abandoned</c> or <c>erased</c>.</summary>
    [MaxLength(30)]
    public string? DeletionReason { get; set; }
}
