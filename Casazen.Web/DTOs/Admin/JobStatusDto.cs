namespace Casazen.Web.DTOs.Admin;

public class JobStatusDto
{
    public string JobName { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public DateTime? LastRun { get; set; }
    public string LastStatus { get; set; } = "Unknown"; // "Succeeded" | "Failed" | "Processing" | "Enqueued" | "Unknown"
    public DateTime? NextRun { get; set; }
}
