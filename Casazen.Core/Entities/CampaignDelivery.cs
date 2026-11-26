using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Casazen.Core.Entities;

/// <summary>A reserved monthly slot persists before the provider call, including when its outcome is unknown.</summary>
[Index(nameof(CampaignId), nameof(ProfileId), IsUnique = true)]
[Index(nameof(OrgId), nameof(ProfileId), nameof(Month), nameof(Slot), IsUnique = true)]
public class CampaignDelivery : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid ContactId { get; set; }
    public int Month { get; set; }
    public int Slot { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "Reserved";
    public DateTime CreatedAt { get; set; }
    public bool Opened { get; set; }
    public bool Clicked { get; set; }
    public bool Bounced { get; set; }
    public DateTime? UnsubscribedAt { get; set; }
}
