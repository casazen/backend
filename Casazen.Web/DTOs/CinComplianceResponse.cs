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

    /// <summary>
    /// Days from today (Europe/Rome) to <see cref="Deadline"/>: 0 on the deadline day, negative once it has passed, null
    /// when no deadline is configured (CO-20).
    /// </summary>
    public int? DaysUntilDeadline { get; set; }

    /// <summary>Configured CIN deadline (<c>yyyy-MM-dd</c>, <c>Cin:ExposureDeadline</c>), null when none is set.</summary>
    public string? Deadline { get; set; }

    /// <summary><c>none</c> (no deadline configured), <c>upcoming</c>, <c>today</c> or <c>passed</c>.</summary>
    public string DeadlineStatus { get; set; } = "none";

    public bool HasNonCompliant { get; set; }
}

public class CinComplianceResponse
{
    public List<CinComplianceItemResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public CinComplianceSummaryResponse Summary { get; set; } = new();
}
