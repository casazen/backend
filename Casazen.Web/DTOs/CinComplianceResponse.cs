namespace Casazen.Web.DTOs;

public class CinComplianceItemResponse
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string? CinCode { get; set; }
    public string CinStatus { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public class CinComplianceSummaryResponse
{
    public int Valid { get; set; }
    public int Missing { get; set; }
    public int Invalid { get; set; }
    public int DaysUntilDeadline { get; set; }
    public string Deadline { get; set; } = string.Empty;
    public bool HasNonCompliant { get; set; }
}

public class CinComplianceResponse
{
    public List<CinComplianceItemResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public CinComplianceSummaryResponse Summary { get; set; } = new();
}
