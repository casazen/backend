using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("Campaigns")]
public class Campaign : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    public Guid SegmentId { get; set; }
    public virtual GuestSegment? Segment { get; set; }

    [Required]
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string Body { get; set; } = string.Empty;

    public bool IsAiGenerated { get; set; } = false;

    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

    public DateTime? ScheduledAt { get; set; }

    public DateTime? SentAt { get; set; }

    public int SentCount { get; set; } = 0;

    public int ErrorCount { get; set; } = 0;

    public int UnsubscribeCount { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
