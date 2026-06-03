namespace Casazen.Web.DTOs.Admin;

public class AdminStatsDto
{
    public int TotalProperties { get; set; }
    public int ActiveProperties { get; set; }
    public int TotalBookings { get; set; }
    public int BookingsThisMonth { get; set; }
    public int UpcomingCheckIns { get; set; }
    public decimal TotalRevenue { get; set; }
    public CinComplianceStats CinCompliance { get; set; } = new();
    public OtaSyncHealth OtaSyncHealth { get; set; } = new();
}

public class CinComplianceStats
{
    public int Valid { get; set; }
    public int Missing { get; set; }
    public int Invalid { get; set; }
    public int Total { get; set; }
}

public class OtaSyncHealth
{
    public int Synced { get; set; }
    public int Failed { get; set; }
    public int NeverSynced { get; set; }
}
