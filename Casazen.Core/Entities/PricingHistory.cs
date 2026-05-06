using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Audit trail of every AI-driven price change for a property.
/// Records previous and new prices, confidence scores, and OTA sync status
/// as part of the AI-Driven Dynamic Pricing Engine.
/// Part of Epic: AI-Driven Dynamic Pricing Engine.
/// </summary>
[Table("PricingHistories")]
public class PricingHistory
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

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
    /// AI model confidence score for this pricing decision, between 0 and 1.
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

    /// <summary>
    /// UTC timestamp of when this pricing history record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
