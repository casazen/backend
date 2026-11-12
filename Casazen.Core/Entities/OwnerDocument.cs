using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A document an agency shares with one of its owners in the owner portal (E07/F07/US01/T02, AC-NF111-3): a mandate, a contract, an insurance, a
/// statement or an invoice, or another document named by its title, for the owner profile and, when it says so, for one of its properties. Its files are
/// <see cref="OwnerDocumentVersion"/> rows, immutable; every download is an <see cref="OwnerDocumentDownload"/> row. The statements the owner accounting
/// publishes (E06) are not copied here: the portal reads their published versions where they are.
/// </summary>
[Table("OwnerDocuments")]
public class OwnerDocument : ITenantOwned
{
    public const int TitleMaxLength = 160;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the agency. Server-set.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The owner profile of the agency it is shared with (<c>PropertyOwner.Id</c>).</summary>
    public Guid PropertyOwnerId { get; set; }

    /// <summary>The property it is about; <c>null</c> for a document of the owner profile (every property of it).</summary>
    public Guid? PropertyId { get; set; }

    public OwnerDocumentType Type { get; set; } = OwnerDocumentType.Other;

    /// <summary>What the agency calls it, shown to the owner.</summary>
    [Required]
    [MaxLength(TitleMaxLength)]
    public string Title { get; set; } = string.Empty;

    /// <summary>When the agency withdrew it from the portal: the owner stops seeing it at the next request. <c>null</c> while shared.</summary>
    public DateTime? RevokedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? RevokedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
