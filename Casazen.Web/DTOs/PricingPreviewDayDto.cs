namespace Casazen.Web.DTOs;

/// <summary>
/// Suggested price for a single day in a pricing preview.
/// </summary>
public class PricingPreviewDayDto
{
    /// <summary>Calendar date in <c>yyyy-MM-dd</c> format.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>AI-suggested nightly rate for this date (EUR).</summary>
    public decimal SuggestedPrice { get; set; }

    /// <summary>Property's current base nightly rate (EUR), before multipliers.</summary>
    public decimal BasePrice { get; set; }

    /// <summary>Short explanation of the factors that drove the suggested price.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 90-day forward-looking pricing preview for a property.
/// </summary>
public class PricingPreviewResponse
{
    /// <summary>Suggested daily prices for the next 90 days.</summary>
    public IEnumerable<PricingPreviewDayDto> Prices { get; set; } = Enumerable.Empty<PricingPreviewDayDto>();
}
