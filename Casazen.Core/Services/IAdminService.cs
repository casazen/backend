namespace Casazen.Core.Services;

/// <summary>Platform statistics for the admin dashboard.</summary>
public record AdminStats(
    int TotalProperties,
    int ActiveProperties,
    int TotalBookings,
    int BookingsThisMonth,
    int UpcomingCheckIns,
    decimal TotalRevenue,
    int CinValid,
    int CinMissing,
    int CinInvalid,
    int CinTotal,
    int OtaSynced,
    int OtaFailed,
    int OtaNeverSynced);

/// <summary>Single property row in the CIN compliance report.</summary>
public record CinComplianceItem(
    Guid PropertyId,
    string PropertyName,
    string OwnerId,
    string OwnerEmail,
    string? CinCode,
    string CinStatus,
    string City);

/// <summary>Hangfire recurring-job status row.</summary>
public record JobStatus(
    string JobName,
    string CronExpression,
    DateTime? LastRun,
    string LastStatus,
    DateTime? NextRun);

public interface IAdminService
{
    Task<AdminStats> GetStatsAsync();

    Task<(IEnumerable<CinComplianceItem> Items, int TotalCount)> GetCinComplianceAsync(
        string? cinStatus, int page, int pageSize);

    Task<IEnumerable<JobStatus>> GetJobStatusesAsync();
}
