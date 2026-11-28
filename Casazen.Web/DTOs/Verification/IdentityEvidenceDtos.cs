using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;

namespace Casazen.Web.DTOs.Verification;

public sealed class IdentityEvidenceItemDto
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public static IdentityEvidenceItemDto From(IdentityEvidenceItem item) => new()
    {
        Id = item.Id,
        Kind = IdentityEvidenceKinds.Code(item.Kind),
        CreatedAt = item.CreatedAt,
        ExpiresAt = item.ExpiresAt,
    };
}

public sealed class PendingEvidenceDto
{
    public int Count { get; set; }

    public DateTime ExpiresAt { get; set; }

    public static PendingEvidenceDto? From(PendingEvidenceSummary? summary) =>
        summary is null ? null : new PendingEvidenceDto { Count = summary.Count, ExpiresAt = summary.ExpiresAt };
}
