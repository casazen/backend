using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// Audit of the legal review of the SEO pages (SE-01, A8-04): who approved or withdrew which revision, when, and why.
/// Append-only. Platform content managed by admins, no organization.
/// </summary>
[Table("SeoContentReviewEvents")]
public class SeoContentReviewEvent
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PageId { get; set; }

    /// <summary>The approved revision, or the one that was published when the page was withdrawn (none if it had none).</summary>
    public Guid? RevisionId { get; set; }

    public SeoReviewAction Action { get; set; }

    /// <summary>Auth0 subject of the admin, or <c>system:&lt;task&gt;</c> for a migration.</summary>
    [Required, MaxLength(200)]
    public string ActorUserId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>The admin confirmed the legal review of the text (required for a page with <c>CounselRequired</c>).</summary>
    public bool CounselApproved { get; set; }

    [MaxLength(1000)]
    public string? Note { get; set; }
}
