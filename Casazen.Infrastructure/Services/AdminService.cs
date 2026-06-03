using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class AdminService(
    AppDbContext dbContext,
    ILogger<AdminService> logger) : IAdminService
{
    private const string CinPattern = @"^IT-\d{5}-\d{10}$";
    private static readonly TimeSpan OtaSyncThreshold = TimeSpan.FromHours(6);

    public async Task<AdminStats> GetStatsAsync()
    {
        var now = DateTime.UtcNow;
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Properties
        var allProperties = await dbContext.Properties.ToListAsync();
        var totalProperties = allProperties.Count;
        var activeProperties = allProperties.Count(p => p.IsActive);

        // CIN compliance
        var cinValid = allProperties.Count(p => !string.IsNullOrWhiteSpace(p.CinCode) && Regex.IsMatch(p.CinCode, CinPattern));
        var cinMissing = allProperties.Count(p => string.IsNullOrWhiteSpace(p.CinCode));
        var cinInvalid = allProperties.Count(p => !string.IsNullOrWhiteSpace(p.CinCode) && !Regex.IsMatch(p.CinCode, CinPattern));
        var cinTotal = totalProperties;

        // Bookings — server-side aggregates to avoid loading full table
        var totalBookings = await dbContext.Bookings.CountAsync();
        var bookingsThisMonth = await dbContext.Bookings.CountAsync(b => b.CreatedAt >= startOfMonth);
        var upcomingCheckIns = await dbContext.Bookings.CountAsync(b =>
            b.Status == BookingStatus.Confirmed && b.CheckInDate >= now);

        // Revenue — sum of completed payments
        var totalRevenue = await dbContext.Payments
            .Where(p => p.Status == PaymentStatus.Completed)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

        // OTA sync health — server-side aggregates; DateTime.MinValue means never synced
        var otaSyncCutoff = now - OtaSyncThreshold;
        var otaNever = await dbContext.OtaIntegrations.CountAsync(o => o.LastSyncAt == default);
        var otaSynced = await dbContext.OtaIntegrations.CountAsync(o =>
            o.LastSyncAt != default && o.LastSyncAt >= otaSyncCutoff);
        var otaFailed = await dbContext.OtaIntegrations.CountAsync(o =>
            o.LastSyncAt != default && o.LastSyncAt < otaSyncCutoff);

        return new AdminStats(
            TotalProperties: totalProperties,
            ActiveProperties: activeProperties,
            TotalBookings: totalBookings,
            BookingsThisMonth: bookingsThisMonth,
            UpcomingCheckIns: upcomingCheckIns,
            TotalRevenue: totalRevenue,
            CinValid: cinValid,
            CinMissing: cinMissing,
            CinInvalid: cinInvalid,
            CinTotal: cinTotal,
            OtaSynced: otaSynced,
            OtaFailed: otaFailed,
            OtaNeverSynced: otaNever);
    }

    public async Task<(IEnumerable<CinComplianceItem> Items, int TotalCount)> GetCinComplianceAsync(
        string? cinStatus, int page, int pageSize)
    {
        // Validate cinStatus
        if (!string.IsNullOrWhiteSpace(cinStatus) &&
            cinStatus != "valid" && cinStatus != "missing" && cinStatus != "invalid")
        {
            throw new ArgumentException($"Unknown cinStatus value '{cinStatus}'", nameof(cinStatus));
        }

        var properties = await dbContext.Properties.ToListAsync();

        // Resolve owner emails from user table (best-effort; user may not exist in DB yet)
        var ownerIds = properties.Select(p => p.OwnerId).Distinct().ToList();
        var userMap = (await dbContext.Users
                .Where(u => ownerIds.Contains(u.Id))
                .ToListAsync())
            .ToDictionary(u => u.Id, u => u.Email);

        IEnumerable<CinComplianceItem> items = properties.Select(p =>
        {
            var status = string.IsNullOrWhiteSpace(p.CinCode) ? "missing"
                : Regex.IsMatch(p.CinCode, CinPattern) ? "valid"
                : "invalid";

            return new CinComplianceItem(
                PropertyId: p.Id,
                PropertyName: p.Name,
                OwnerId: p.OwnerId,
                OwnerEmail: userMap.GetValueOrDefault(p.OwnerId, "unknown"),
                CinCode: p.CinCode,
                CinStatus: status,
                City: p.City);
        });

        if (!string.IsNullOrWhiteSpace(cinStatus))
            items = items.Where(i => i.CinStatus == cinStatus);

        var list = items.ToList();
        var totalCount = list.Count;
        var paged = list
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return (paged, totalCount);
    }

    public Task<IEnumerable<JobStatus>> GetJobStatusesAsync()
    {
        var results = new List<JobStatus>();

        try
        {
            var monitoringApi = JobStorage.Current.GetMonitoringApi();
            var recurringJobs = JobStorage.Current.GetConnection().GetRecurringJobs();

            foreach (var job in recurringJobs)
            {
                DateTime? lastRun = job.LastExecution;
                DateTime? nextRun = job.NextExecution;
                string lastStatus = "Unknown";

                if (!string.IsNullOrEmpty(job.LastJobId))
                {
                    try
                    {
                        var jobDetails = monitoringApi.JobDetails(job.LastJobId);
                        if (jobDetails?.History != null && jobDetails.History.Count > 0)
                        {
                            lastStatus = jobDetails.History[0].StateName ?? "Unknown";
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not retrieve job details for {JobId}", job.LastJobId);
                    }
                }

                results.Add(new JobStatus(
                    JobName: job.Id ?? "Unknown",
                    CronExpression: job.Cron ?? string.Empty,
                    LastRun: lastRun,
                    LastStatus: lastStatus,
                    NextRun: nextRun));
            }
        }
        catch (Exception ex)
        {
            // Hangfire storage may not be available in test env (in-memory DB)
            logger.LogWarning(ex, "Could not retrieve Hangfire job statuses");
        }

        return Task.FromResult<IEnumerable<JobStatus>>(results);
    }
}
