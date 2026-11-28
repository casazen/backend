using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A link from a version of an owner statement to the invoice the agency's external fiscal or accounting system issued for its fee (E06/F01/US02/T05,
/// ADR-019 amended, AC-NF110-6): the provider, the immutable id, number and date the provider gave it, where the document is and its SHA-256. CasaZen
/// stores the link only: it has no amount, VAT, numbering or submission state of its own and never changes the statement. A mistaken link is removed
/// with its reason and stays as evidence (never deleted, <c>AppendOnlyGuard</c>); the owners see it only once the statement is published.
/// </summary>
[Table("OwnerExternalDocumentReferences")]
public class OwnerExternalDocumentReference : ITenantOwned
{
    public const int ProviderMaxLength = 100;
    public const int ExternalIdMaxLength = 200;
    public const int NumberMaxLength = 100;
    public const int UrlMaxLength = 1000;
    public const int HashLength = 64;
    public const int ReasonMaxLength = 500;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid OwnerStatementVersionId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    [Required]
    [MaxLength(ProviderMaxLength)]
    public string Provider { get; set; } = string.Empty;

    [Required]
    [MaxLength(ExternalIdMaxLength)]
    public string ExternalId { get; set; } = string.Empty;

    [Required]
    [MaxLength(NumberMaxLength)]
    public string ExternalNumber { get; set; } = string.Empty;

    public DateOnly ExternalDate { get; set; }

    [MaxLength(UrlMaxLength)]
    public string? DocumentUrl { get; set; }

    [MaxLength(HashLength)]
    public string? DocumentSha256 { get; set; }

    public OwnerExternalDocumentStatus Status { get; set; } = OwnerExternalDocumentStatus.Entered;

    public DateTime? VerifiedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? VerifiedByUserId { get; set; }

    public DateTime? RemovedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? RemovedByUserId { get; set; }

    [MaxLength(ReasonMaxLength)]
    public string? RemovalReason { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
