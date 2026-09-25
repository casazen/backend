using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Log of the batch price pushes to the OTA partner APIs (<c>IOtaManager.BatchUpdatePricingAsync</c>, in freeze behind
/// <c>Features:OtaPartnerApi</c>). The seasonal price suggestions (PC-15) never write here: their rows are
/// <see cref="SeasonalPriceSuggestion"/>. The rows invented by the old "AI pricing" job (base 100 EUR, confidence 0.85) were
/// deleted by the migration <c>SeasonalPriceSuggestions</c>.
/// </summary>
[Table("PricingHistories")]
public class PricingHistory : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Property"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// UTC timestamp of when this pricing adaptation was applied.
    /// </summary>
    public DateTime AdaptationDate { get; set; } = DateTime.UtcNow;

    [Precision(18, 2)]
    public decimal PreviousPrice { get; set; }

    [Precision(18, 2)]
    public decimal NewPrice { get; set; }

    /// <summary>
    /// Human-readable explanation for the price change (e.g. "High demand period", "Low occupancy").
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string ChangeReason { get; set; } = string.Empty;

    /// <summary>
    /// Legacy column, between 0 and 1: no model computes it (the OTA batch push writes 1). Never shown to users.
    /// </summary>
    [Precision(5, 4)]
    [Range(0.0, 1.0)]
    public decimal AiConfidence { get; set; }

    /// <summary>
    /// JSON-serialized list of OTA platform names that received this price update.
    /// </summary>
    [MaxLength(2000)]
    public string OtasSynced { get; set; } = string.Empty;

    /// <summary>
    /// Sync status of this price change across OTA platforms (e.g. "Pending", "Synced", "Failed").
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string SyncStatus { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
