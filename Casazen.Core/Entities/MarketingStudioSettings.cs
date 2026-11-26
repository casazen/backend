using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

public class MarketingStudioSettings : ITenantOwned
{
    [Key]
    public Guid OrgId { get; set; }
    public int MonthlyEmailLimit { get; set; } = 2;
    public int LoyalStays { get; set; } = 3;
    public int LostMonths { get; set; } = 24;
    public string Voice { get; set; } = string.Empty;
    [Column(TypeName = "jsonb")]
    public string PropertyNotesJson { get; set; } = "{}";
    [Column(TypeName = "jsonb")]
    public string PropertyUrlsJson { get; set; } = "{}";
    [Column(TypeName = "jsonb")]
    public string NewsletterJson { get; set; } = "{}";
    public DateTime? NextNewsletterAt { get; set; }
    public DateTime? NextEventSearchAt { get; set; }
}

[Index(nameof(OrgId), nameof(Occurrence), IsUnique = true)]
[Index(nameof(OrgId), nameof(PropertyId), nameof(CreatedAt))]
public class MarketingAsset : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid SegmentId { get; set; }
    public string? Occurrence { get; set; }
    public Guid? CampaignId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Keywords { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Budget { get; set; } = string.Empty;
    public string? Destination { get; set; }
    public bool ExternalDestination { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public int Clicks { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Index(nameof(Locality), nameof(From), nameof(To), IsUnique = true)]
public class MarketingEventSearch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Locality { get; set; } = string.Empty;
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public DateTime CheckedAt { get; set; }
    [Column(TypeName = "jsonb")]
    public string EventsJson { get; set; } = "[]";
}

[PrimaryKey(nameof(OrgId), nameof(Month))]
[Table("MarketingAiUsage")]
public sealed class MarketingAiUsage : ITenantOwned
{
    public Guid OrgId { get; set; }
    public DateOnly Month { get; set; }
    public int Requests { get; set; }
}