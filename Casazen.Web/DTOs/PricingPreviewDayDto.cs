namespace Casazen.Web.DTOs;

public class PricingPreviewDayDto
{
    public string Date { get; set; } = string.Empty;
    public decimal SuggestedPrice { get; set; }
    public decimal BasePrice { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class PricingPreviewResponse
{
    public IEnumerable<PricingPreviewDayDto> Prices { get; set; } = Enumerable.Empty<PricingPreviewDayDto>();
}
