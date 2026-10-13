using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Official tourist-tax profile of one Italian comune (ISTAT code), shared by every property in that comune.
/// Platform reference data: no org. Versions keep history; this row points at the current one.
/// </summary>
[Table("ComuneOfficialProfiles")]
public class ComuneOfficialProfile
{
    /// <summary>ISTAT code of the comune, 6 digits. Logical FK to <see cref="Comune.IstatCode"/> without cascade.</summary>
    [Key, MaxLength(6)]
    public string IstatCode { get; set; } = string.Empty;

    public Guid? CurrentVersionId { get; set; }

    public DateTime LastEnsuredAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<ComuneOfficialProfileVersion> Versions { get; set; } = [];
}

/// <summary>
/// One retrieval of the MEF act for a comune. Previous versions are never deleted; only one row per comune is current.
/// </summary>
[Table("ComuneOfficialProfileVersions")]
public class ComuneOfficialProfileVersion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(6)]
    public string IstatCode { get; set; } = string.Empty;

    public int VersionNumber { get; set; }

    public bool IsCurrent { get; set; }

    /// <summary><see cref="ComuneOfficialProfileStatus"/>.</summary>
    [Required, MaxLength(20)]
    public string Status { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ActId { get; set; }

    [MaxLength(64)]
    public string? ActSha256 { get; set; }

    [MaxLength(64)]
    public string? PdfSha256 { get; set; }

    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    [MaxLength(500)]
    public string? IndexUrl { get; set; }

    [MaxLength(200)]
    public string? Authority { get; set; }

    public DateOnly? MefPublishedOn { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    /// <summary>Validated extract JSON; null when the act was unreadable and produced no object.</summary>
    public string? ExtractJson { get; set; }

    [MaxLength(40)]
    public string? PromptVersion { get; set; }

    public DateTime RetrievedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Technical reason; never a document body.</summary>
    [MaxLength(500)]
    public string? Detail { get; set; }
}

/// <summary>Values stored on <see cref="ComuneOfficialProfileVersion.Status"/>.</summary>
public static class ComuneOfficialProfileStatus
{
    public const string Extracted = "extracted";
    public const string Unreadable = "unreadable";
    public const string Unchanged = "unchanged";
    public const string Rejected = "rejected";
}
