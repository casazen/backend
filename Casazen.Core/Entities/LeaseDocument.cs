using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public enum LeaseDocumentKind { Contract, Attestation, Registration, Other, RegistrationIdentity }

[Table("LeaseDocuments")]
public class LeaseDocument : ITenantOwned
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeaseContractId { get; set; }
    public LeaseContract LeaseContract { get; set; } = null!;
    public Guid OrgId { get; set; }
    public LeaseDocumentKind Kind { get; set; }
    [MaxLength(240)] public string FileName { get; set; } = string.Empty;
    [MaxLength(1000)] public string StorageKey { get; set; } = string.Empty;
    [MaxLength(200)] public string UploadedBy { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public LeaseDocumentExtractionStatus ExtractionStatus { get; set; } = LeaseDocumentExtractionStatus.NotStarted;
    public DateTime? ExtractionStartedAt { get; set; }
    public DateTime? ExtractionCompletedAt { get; set; }

    [Column(TypeName = "jsonb")]
    public string? ExtractionResult { get; set; }

    public string? ExtractionError { get; set; }

    [MaxLength(64)]
    public string? DocumentHash { get; set; }
}
