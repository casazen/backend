namespace Casazen.Core.DTOs;

public class PublicPropertyDetailDto : PublicPropertyDto
{
    public string HouseRules { get; set; } = string.Empty;
    public string CancellationPolicySummary { get; set; } = string.Empty;
    public int? MinNights { get; set; }
    public string Currency { get; set; } = "EUR";
}
