using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public sealed class OctoratePreparation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public OctorateEnvironment Environment { get; set; }
    public OctoratePreparationStatus Status { get; set; }
    [MaxLength(64)]
    public string? AccommodationId { get; set; }
    public string? EncryptedRefreshToken { get; set; }
    public DateTime? ProductCreationRequestedAt { get; set; }
    [MaxLength(120)]
    public string? SupplierReference { get; set; }
    [MaxLength(64)]
    public string? ErrorCode { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum OctoratePreparationStatus
{
    Queued,
    ReconcileCreation,
    WaitingForLicense,
    Ready,
    NeedsReview,
}
