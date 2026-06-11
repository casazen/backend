using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

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

    public LegalReviewStatus LegalReviewStatus { get; set; } = LegalReviewStatus.Draft;

    public bool CounselRequired { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? LastRefreshedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SeoContentRevision> Revisions { get; set; } = [];
}
