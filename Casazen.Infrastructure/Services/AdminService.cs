using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class AdminService(
    AppDbContext dbContext,
    ILogger<AdminService> logger,
    TimeProvider? timeProvider = null) : IAdminService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static readonly TimeSpan OtaSyncThreshold = TimeSpan.FromHours(6);

    /// <summary>
    /// Audits an AdminOnly read that deliberately bypasses the global tenant filter
    /// (<c>IgnoreQueryFilters()</c>) to aggregate across every org (#202 F-H1, design §Security
    /// Notes). Mirrors the structured-event style of <c>AdminAccessAuditService</c>.
    /// </summary>
    private void LogPrivilegedCrossOrgRead(string action) =>
        logger.LogWarning(
            "Privileged cross-org admin read: {Event} Action={Action} Scope={Scope} Timestamp={Timestamp}",
            "PrivilegedCrossOrgRead",
            action,
            "platform-wide",
            DateTime.UtcNow);

    public async Task<AdminStats> GetStatsAsync()
    {
        var now = DateTime.UtcNow;
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Platform-wide admin read (AdminOnly). The EF global tenant filter scopes every tenant
        // table to the caller's org; admins typically have no org, so the dashboard would silently
        // go empty. Per design §Security Notes this AdminOnly path BYPASSES the filter with
        // IgnoreQueryFilters() — audited here as privileged cross-org access (#202 F-H1).
        LogPrivilegedCrossOrgRead(nameof(GetStatsAsync));

        // Properties (filter bypassed — platform-wide): counted in SQL, never materialized in full (A1-27).
        var propertiesQuery = dbContext.Properties.IgnoreQueryFilters();
        var totalProperties = await propertiesQuery.CountAsync();
        var activeProperties = await propertiesQuery.CountAsync(p => p.IsActive);

        // CIN compliance: valid/missing counted directly in SQL — Npgsql translates Regex.IsMatch to Postgres' native
        // ~ operator, reusing CinFormat's own pattern constants (single source of truth, A1-27). Invalid is the
        // remainder, so this needs only two full-table scans instead of loading every row into memory.
        var cinValid = await propertiesQuery.CountAsync(p =>
            p.CinCode != null && p.CinCode != "" &&
            Regex.IsMatch(p.CinCode, CinFormat.Pattern) && !Regex.IsMatch(p.CinCode, CinFormat.LegacyPattern));
        var cinMissing = await propertiesQuery.CountAsync(p => p.CinCode == null || p.CinCode == "");
        var cinInvalid = totalProperties - cinValid - cinMissing;
        var cinTotal = totalProperties;

        // Bookings — server-side aggregates to avoid loading full table (filter bypassed — platform-wide)
        var totalBookings = await dbContext.Bookings.IgnoreQueryFilters().CountAsync();
        var bookingsThisMonth = await dbContext.Bookings.IgnoreQueryFilters().CountAsync(b => b.CreatedAt >= startOfMonth);
        // Check-in is date-only: compared with today's date in Europe/Rome, not with the UTC instant (QA-CLOCK).
        var today = _clock.TodayInRome();
        var upcomingCheckIns = await dbContext.Bookings.IgnoreQueryFilters().CountAsync(b =>
            b.Status == BookingStatus.Confirmed && b.CheckInDate > today);

        // Revenue — sum of completed payments (filter bypassed — platform-wide)
        var totalRevenue = await dbContext.Payments
            .IgnoreQueryFilters()
            .Where(p => p.Status == PaymentStatus.Completed)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

        // OTA sync health — server-side aggregates; DateTime.MinValue means never synced
        // (filter bypassed — platform-wide; OtaIntegrations is tenant-scoped since TN-2)
        var otaSyncCutoff = now - OtaSyncThreshold;
        var otaNever = await dbContext.OtaIntegrations.IgnoreQueryFilters().CountAsync(o => o.LastSyncAt == default);
        var otaSynced = await dbContext.OtaIntegrations.IgnoreQueryFilters().CountAsync(o =>
            o.LastSyncAt != default && o.LastSyncAt >= otaSyncCutoff);
        var otaFailed = await dbContext.OtaIntegrations.IgnoreQueryFilters().CountAsync(o =>
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

        // A page below 1 would turn into a negative OFFSET, which Postgres rejects with a 500 (A1-26).
        page = Math.Max(page, 1);

        // Platform-wide admin read (AdminOnly): the CIN-compliance (D.L. 145/2023) report covers
        // every org, so it BYPASSES the global tenant filter with an audit line (#202 F-H1).
        LogPrivilegedCrossOrgRead(nameof(GetCinComplianceAsync));

        var query = dbContext.Properties.IgnoreQueryFilters().AsQueryable();

        // Filtered and paginated in SQL (A1-27): this used to load every property into memory. The regex reuses
        // CinFormat's own pattern constants (single source of truth, .claude/rules/compliance.md) — Npgsql
        // translates Regex.IsMatch to Postgres' native ~ operator, so the match still runs server-side.
        query = cinStatus switch
        {
            "missing" => query.Where(p => p.CinCode == null || p.CinCode == ""),
            "valid" => query.Where(p =>
                p.CinCode != null && p.CinCode != "" &&
                Regex.IsMatch(p.CinCode, CinFormat.Pattern) && !Regex.IsMatch(p.CinCode, CinFormat.LegacyPattern)),
            "invalid" => query.Where(p =>
                p.CinCode != null && p.CinCode != "" &&
                (!Regex.IsMatch(p.CinCode, CinFormat.Pattern) || Regex.IsMatch(p.CinCode, CinFormat.LegacyPattern))),
            _ => query,
        };

        var totalCount = await query.CountAsync();

        var pageItems = await query
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new { p.Id, p.Name, p.OwnerId, p.CinCode, p.City })
            .ToListAsync();

        // Owner emails resolved only for this page (best-effort; user may not exist in DB yet), not for every
        // property on the platform.
        var ownerIds = pageItems.Select(p => p.OwnerId).Distinct().ToList();
        var userMap = (await dbContext.Users
                .Where(u => ownerIds.Contains(u.Id))
                .ToListAsync())
            .ToDictionary(u => u.Id, u => u.Email);

        var items = pageItems
            .Select(p => new CinComplianceItem(
                PropertyId: p.Id,
                PropertyName: p.Name,
                OwnerId: p.OwnerId,
                OwnerEmail: userMap.GetValueOrDefault(p.OwnerId, "unknown"),
                CinCode: p.CinCode,
                CinStatus: CinComplianceRules.ResolveStatus(p.CinCode),
                City: p.City))
            .ToList();

        return (items, totalCount);
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
                        // A single job's detail lookup failing does not invalidate the whole listing.
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
        catch (InvalidOperationException ex)
        {
            // JobStorage.Current itself throws only InvalidOperationException, and only when no storage was ever
            // registered (AddCasazenHangfire, e.g. dev/test without a DB connection string): "no jobs" is then the
            // honest answer, not a failure (A1-26). Any OTHER exception (Postgres unreachable, timeout…) is a
            // genuine infrastructure problem and is deliberately left to propagate, so the admin sees an error
            // instead of a silently empty list.
            logger.LogInformation(ex, "Hangfire storage not configured: no job statuses to report");
            return Task.FromResult(Enumerable.Empty<JobStatus>());
        }

        return Task.FromResult<IEnumerable<JobStatus>>(results);
    }
}
