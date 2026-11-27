using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("SeoContentReviewEvents")]
public class SeoContentReviewEvent
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PageId { get; set; }

    public Guid? RevisionId { get; set; }

    public SeoReviewAction Action { get; set; }

    [Required, MaxLength(200)]
    public string ActorUserId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public bool CounselApproved { get; set; }

    [MaxLength(1000)]
    public string? Note { get; set; }
}
