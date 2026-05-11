namespace Casazen.Web.DTOs;

public class PricingHistoryDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public DateTime AdaptationDate { get; set; }
    public decimal PreviousPrice { get; set; }
    public decimal NewPrice { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public decimal AiConfidence { get; set; }
    public string OtasSynced { get; set; } = string.Empty;
    public string SyncStatus { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class PricingHistoryPagedResponse
{
    public IEnumerable<PricingHistoryDto> Items { get; set; } = Enumerable.Empty<PricingHistoryDto>();
    public int Total { get; set; }
    public int Page { get; set; }
}
