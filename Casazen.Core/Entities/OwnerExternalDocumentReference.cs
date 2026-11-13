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

    /// <summary>The owner of the version, kept for the owner's reading.</summary>
    public Guid PropertyOwnerId { get; set; }

    /// <summary>The external system that issued the document (its name as the agency calls it).</summary>
    [Required]
    [MaxLength(ProviderMaxLength)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>The immutable id of the document in the external system.</summary>
    [Required]
    [MaxLength(ExternalIdMaxLength)]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The number the external system gave the document (CasaZen assigns none).</summary>
    [Required]
    [MaxLength(NumberMaxLength)]
    public string ExternalNumber { get; set; } = string.Empty;

    public DateOnly ExternalDate { get; set; }

    /// <summary>Where the document issued by the external system is (an https address), when the agency links it.</summary>
    [MaxLength(UrlMaxLength)]
    public string? DocumentUrl { get; set; }

    /// <summary>The SHA-256 of the document, as the agency computed it on the issued file.</summary>
    [MaxLength(HashLength)]
    public string? DocumentSha256 { get; set; }

    public OwnerExternalDocumentStatus Status { get; set; } = OwnerExternalDocumentStatus.Entered;

    public DateTime? VerifiedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? VerifiedByUserId { get; set; }

    /// <summary>When the link was removed as mistaken; the row stays.</summary>
    public DateTime? RemovedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? RemovedByUserId { get; set; }

    [MaxLength(ReasonMaxLength)]
    public string? RemovalReason { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
