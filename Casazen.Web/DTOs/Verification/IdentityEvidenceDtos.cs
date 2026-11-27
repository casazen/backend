using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;

namespace Casazen.Web.DTOs.Verification;

/// <summary>
/// One picture of the evidence kept for the reviewer of an identity verification (E73/F01/US02/T02, <c>GET api/Verification/{bookingId}/evidence</c>): its id, its kind
/// (<c>documentPortrait</c> or <c>frame</c>) and its times (UTC). The picture itself is read from <c>GET api/Verification/{bookingId}/evidence/{id}</c>.
/// </summary>
public sealed class IdentityEvidenceItemDto
{
    public Guid Id { get; set; }

    /// <summary><c>documentPortrait</c> (the crop of the portrait of the document) or <c>frame</c> (a frame of the presence chosen by the server).</summary>
    public string Kind { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>When the picture is deleted at the latest; it is deleted before when an operator decides.</summary>
    public DateTime ExpiresAt { get; set; }

    public static IdentityEvidenceItemDto From(IdentityEvidenceItem item) => new()
    {
        Id = item.Id,
        Kind = IdentityEvidenceKinds.Code(item.Kind),
        CreatedAt = item.CreatedAt,
        ExpiresAt = item.ExpiresAt,
    };
}

/// <summary>The evidence of a booking in the operator queue (E73/F01/US02/T02): how many pictures are kept and when the first of them is deleted at the latest (UTC).</summary>
public sealed class PendingEvidenceDto
{
    public int Count { get; set; }

    public DateTime ExpiresAt { get; set; }

    public static PendingEvidenceDto? From(PendingEvidenceSummary? summary) =>
        summary is null ? null : new PendingEvidenceDto { Count = summary.Count, ExpiresAt = summary.ExpiresAt };
}
