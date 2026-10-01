using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// A public SEO page of a comune (US-020). The public (page, hub, sitemap) sees <b>only</b> the revision an admin approved,
/// <see cref="PublishedRevisionId"/>; a page without one is not public (SE-01, A8-04, A8-05).
/// </summary>
[Table("SeoContentPages")]
public class SeoContentPage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(300)]
    public string Slug { get; set; } = string.Empty;

    [Required, MaxLength(10)]
    public string ComuneCode { get; set; } = string.Empty;

    [Required, MaxLength(10)]
    public string RegionCode { get; set; } = string.Empty;

    public SeoPageType PageType { get; set; }

    [Required, MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string MetaDescription { get; set; } = string.Empty;

    /// <summary>
    /// Review state of the <b>latest</b> revision: <c>Reviewed</c> when it is the approved, published one; <c>Draft</c>
    /// when it still waits for a review (a new page, a regeneration or an edit), even if an older revision is published.
    /// Written only by <c>ISeoContentRepository</c> (a new revision sets Draft, an approval Reviewed, a withdrawal Draft).
    /// </summary>
    public LegalReviewStatus LegalReviewStatus { get; set; } = LegalReviewStatus.Draft;

    /// <summary>The approved revision the public sees; <c>null</c>: the page is not public (never approved, or withdrawn).</summary>
    public Guid? PublishedRevisionId { get; set; }

    public SeoContentRevision? PublishedRevision { get; set; }

    public bool CounselRequired { get; set; }

    /// <summary>When <see cref="PublishedRevisionId"/> was approved; <c>null</c> when the page is not public.</summary>
    public DateTime? PublishedAt { get; set; }

    public DateTime? LastRefreshedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SeoContentRevision> Revisions { get; set; } = [];
}
