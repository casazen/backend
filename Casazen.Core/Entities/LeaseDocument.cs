using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public enum LeaseDocumentKind { Contract, Attestation, Registration, Other }

/// <summary>An original attachment of one lease, never a property document.</summary>
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

    // Document extraction fields (ADR-009)
    public LeaseDocumentExtractionStatus ExtractionStatus { get; set; } = LeaseDocumentExtractionStatus.NotStarted;
    public DateTime? ExtractionStartedAt { get; set; }
    public DateTime? ExtractionCompletedAt { get; set; }

    /// <summary>
    /// Extraction result in JSONB format: {fields: [{name, value, confidence}], conflicts: [{field, expected, extracted, reason}]}
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ExtractionResult { get; set; }

    public string? ExtractionError { get; set; }

    /// <summary>SHA-256 hash of the file content for duplicate detection.</summary>
    [MaxLength(64)]
    public string? DocumentHash { get; set; }
}
