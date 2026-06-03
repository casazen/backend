namespace Casazen.Web.DTOs;

/// <summary>
/// A single AI-driven price change event recorded for a property.
/// </summary>
public class PricingHistoryDto
{
    /// <summary>Unique identifier of this history record.</summary>
    public Guid Id { get; set; }

    /// <summary>The property this price change applies to.</summary>
    public Guid PropertyId { get; set; }

    /// <summary>UTC date when the pricing adaptation was computed.</summary>
    public DateTime AdaptationDate { get; set; }

    /// <summary>Nightly rate before the adaptation (EUR).</summary>
    public decimal PreviousPrice { get; set; }

    /// <summary>Nightly rate after the adaptation (EUR).</summary>
    public decimal NewPrice { get; set; }

    /// <summary>Human-readable explanation of why the price changed.</summary>
    public string ChangeReason { get; set; } = string.Empty;

    /// <summary>AI model confidence score between 0.0 and 1.0.</summary>
    public decimal AiConfidence { get; set; }

    /// <summary>Comma-separated list of OTA platforms the new price was synced to.</summary>
    public string OtasSynced { get; set; } = string.Empty;

    /// <summary>OTA sync outcome: <c>synced</c>, <c>partial</c>, or <c>failed</c>.</summary>
    public string SyncStatus { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this record was created.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Paginated collection of pricing history records.
/// </summary>
public class PricingHistoryPagedResponse
{
    /// <summary>Price change records for the requested page.</summary>
    public IEnumerable<PricingHistoryDto> Items { get; set; } = Enumerable.Empty<PricingHistoryDto>();

    /// <summary>Total number of records matching the filter (across all pages).</summary>
    public int Total { get; set; }

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; set; }
}
