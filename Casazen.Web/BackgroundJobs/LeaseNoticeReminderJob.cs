using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily reminder to the landlord of the last day to give notice of a long-term lease (LR-11c-a). A canone libero (4+4) or concordato (3+2)
/// lease that nobody gives notice of renews by itself for a whole new period (LR-14), so a landlord who misses the last day has four more
/// years (two for the concordato) of a contract they may not want. That day is already in the agenda and the overview of the area; this is
/// the one place that tells the landlord before it comes.
/// </summary>
/// <remarks>
/// <para><b>Which leases.</b> The ones the agenda lists a <c>Notice</c> deadline for: registered and not ended (<see cref="LeaseListView.Active"/>),
/// of a type that has a notice, with no notice recorded (<see cref="LeaseContract.NoticeOn"/>) and not ended early
/// (<see cref="LeaseContract.TerminatedOn"/>). Never a draft, a lease still to be registered, a concluded one, a transitory one, or one
/// whose last day has passed. The day is <see cref="LongRentDeadlineRules.NoticeDate"/> of the end of the current period
/// (<see cref="LeaseContract.CurrentTermEndDate"/>): the same function and the same date as Scadenze and Panoramica, worked out nowhere
/// else. A lease that renews by itself has a new end, so a new last day and a new set of reminders.</para>
/// <para><b>When.</b> Thresholds, not exact days (<see cref="Thresholds"/>): 30 days before the last day, 7 days before and the day itself.
/// The most urgent threshold reached today is sent once per last day, so a skipped run (or a day without one) is caught up at the next
/// and two runs, a retry or a manual trigger never send it twice. A lease first seen 5 days before gets the 7-day reminder, with the days
/// it really has left. Each reminder sent is recorded as a <c>DeadlineReminderSent</c> event with payload
/// <c>notice-t-30:2027-04-15:{partyId}</c> (threshold, last day, landlord), only when the provider accepted the email: a failed send is
/// tried again at the next run. The payloads start with <c>notice-</c>, so they never meet the ones of <see cref="RliDeadlineReminderJob"/>
/// in the same lease history.</para>
/// <para><b>To whom.</b> Every landlord of the lease with an address that is not anonymized, one email per address, like the RLI reminders
/// and the rent notices: <b>never the tenant</b>. The email names the property, the day, the days left and when the period ends: no name
/// of the tenant, no amount. It does not send the notice nor judge its validity (decision LR-D29).</para>
/// <para>At most <see cref="MaxLeasesPerRun"/> leases a run, the nearest last day first. Logs carry ids and counts only. No setting and
/// no flag: the job is always registered. Runbooks: <c>docs/runbooks/long-rent-aggregates.md</c> (§ 10.8), <c>docs/runbooks/hangfire.md</c>.</para>
/// </remarks>
public class LeaseNoticeReminderJob(
    AppDbContext db,
    IEmailService emailService,
    ILogger<LeaseNoticeReminderJob> logger,
    TimeProvider? timeProvider = null)
{
    public const string RecurringJobId = "lease-notice-reminder";

    /// <summary>Every day at 08:30 UTC (09:30 or 10:30 in Italy), after the hourly renewal of the leases, which runs at minute 15.</summary>
    public const string Cron = "30 8 * * *";

    /// <summary>Leases reminded at most in one run, the nearest last day first; the next run takes the rest.</summary>
    public const int MaxLeasesPerRun = 500;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        var today = _clock.TodayInRomeAsDateOnly();

        var due = await DueLeaseIdsAsync(today);
        if (due.Count > MaxLeasesPerRun)
        {
            logger.LogWarning(
                "Lease notice reminders: {Due} leases are due, only the {Max} with the nearest last day are handled in this run",
                due.Count,
                MaxLeasesPerRun);
            due = due.Take(MaxLeasesPerRun).ToList();
        }

        var emails = 0;
        foreach (var leaseId in due)
        {
            try
            {
                emails += await RemindAsync(leaseId, today);
            }
            catch (Exception ex)
            {
                // One lease that fails (the database, the provider) must not stop the ones behind it; the next run tries it again.
                logger.LogError(ex, "Failed lease notice reminder for LeaseId={LeaseId}", leaseId);
            }
            finally
            {
                // Nothing of one lease stays tracked for the next, and an event that could not be saved is not saved again with it.
                db.ChangeTracker.Clear();
            }
        }

        if (due.Count > 0)
            logger.LogInformation("Lease notice reminders: {Leases} leases due, {Emails} emails sent", due.Count, emails);
    }

    /// <summary>
    /// The leases that can still give notice (registered and not ended, no notice, not ended early, parties not anonymized), read across
    /// every org: the job has no tenant, on purpose.
    /// </summary>
    private IQueryable<LeaseContract> LeasesThatCanGiveNotice(DateOnly today) =>
        db.LeaseContracts
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(LeaseListViews.Predicate(LeaseListView.Active, today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)))
            .Where(l => l.NoticeOn == null && l.TerminatedOn == null && l.PartiesAnonymizedAt == null);

    /// <summary>
    /// The ids of the leases whose last day to give notice is within the first threshold, the nearest day first. A few scalars per lease;
    /// the last day comes from <see cref="LongRentDeadlineRules.NoticeDate"/>, which also tells the types that have none.
    /// </summary>
    private async Task<List<Guid>> DueLeaseIdsAsync(DateOnly today)
    {
        var leases = await LeasesThatCanGiveNotice(today)
            .Select(l => new { l.Id, l.ContractType, l.CurrentTermEndDate })
            .ToListAsync();

        return leases
            .Select(l => (l.Id, LastDay: LongRentDeadlineRules.NoticeDate(l.ContractType, l.CurrentTermEndDate)))
            .Where(l => l.LastDay is { } lastDay && Thresholds.Reached(lastDay.DayNumber - today.DayNumber) is not null)
            .OrderBy(l => l.LastDay)
            .ThenBy(l => l.Id)
            .Select(l => l.Id)
            .ToList();
    }

    /// <summary>
    /// Sends, to every landlord of one lease that has not had it yet, the reminder of the most urgent threshold reached today. The lease is
    /// read again here and must still qualify, so a notice recorded since the list was made stops the reminder. Returns the emails sent.
    /// </summary>
    private async Task<int> RemindAsync(Guid leaseId, DateOnly today)
    {
        var lease = await LeasesThatCanGiveNotice(today)
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Events.Where(e => e.EventType == LeaseEventType.DeadlineReminderSent))
            .FirstOrDefaultAsync(l => l.Id == leaseId);
        if (lease is null || LongRentDeadlineRules.NoticeDate(lease.ContractType, lease.CurrentTermEndDate) is not { } lastDay)
            return 0;

        var daysRemaining = lastDay.DayNumber - today.DayNumber;
        if (Thresholds.Reached(daysRemaining) is not { } threshold)
            return 0;

        var landlords = lease.Parties
            .Where(p => p.Role == PartyRole.Landlord && p.AnonymizedAt == null && !string.IsNullOrWhiteSpace(p.ContactEmail))
            .OrderBy(p => p.Position)
            .DistinctBy(p => p.ContactEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (landlords.Count == 0)
        {
            logger.LogInformation("Skip lease notice reminder {Threshold} for LeaseId={LeaseId}: no landlord email", threshold, lease.Id);
            return 0;
        }

        var payload = Thresholds.Payload(threshold, lastDay);
        var recorded = lease.Events.Where(e => e.Payload is not null).Select(e => e.Payload!).ToHashSet(StringComparer.Ordinal);
        EmailContent? content = null;
        var sent = 0;
        foreach (var landlord in landlords)
        {
            var landlordPayload = $"{payload}:{landlord.Id:N}";
            if (recorded.Contains(landlordPayload))
                continue;

            // Already inside a Hangfire job: sent directly, not through the queue (FD-13).
            content ??= EmailTemplates.LeaseNoticeReminder(
                EmailTemplates.DefaultCulture,
                lease.Property?.Name ?? string.Empty,
                lastDay,
                daysRemaining,
                RomeCalendar.DateInRome(lease.CurrentTermEndDate),
                LongRentRules.RenewalMonths(lease.ContractType));
            var result = await emailService.SendEmailAsync(landlord.ContactEmail.Trim(), content.Subject, content.HtmlBody);
            if (!result.Success)
            {
                // Not recorded: the same threshold is tried again for this landlord at the next run.
                logger.LogWarning(
                    "Lease notice reminder {Threshold} for LeaseId={LeaseId} PartyId={PartyId} not sent: {ErrorDetail}",
                    threshold,
                    lease.Id,
                    landlord.Id,
                    result.ErrorDetail);
                continue;
            }

            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.DeadlineReminderSent,
                OccurredAt = _clock.GetUtcNow().UtcDateTime,
                Payload = landlordPayload,
            });
            await db.SaveChangesAsync();
            sent++;
            logger.LogInformation(
                "Sent {Template} {Threshold} for LeaseId={LeaseId} PartyId={PartyId}",
                EmailTemplates.Names.LeaseNoticeReminder,
                threshold,
                lease.Id,
                landlord.Id);
        }

        return sent;
    }

    /// <summary>
    /// Reminder thresholds of the last day to give notice: 30 days before, 7 days before and the day itself. Product choice, not a
    /// regulatory value (the figures asked for LR-11c-a; the brief of LR-11c recommends 7 days and the day itself).
    /// </summary>
    public static class Thresholds
    {
        public const string Days30 = "notice-t-30";
        public const string Days7 = "notice-t-7";
        public const string LastDay = "notice-t-0";

        /// <summary>
        /// The most urgent threshold reached with <paramref name="daysRemaining"/> days to the last day: the day itself at 0, up to 7, up to
        /// 30; <c>null</c> earlier, and <c>null</c> once the day has passed (nothing is sent after it).
        /// </summary>
        public static string? Reached(int daysRemaining) => daysRemaining switch
        {
            < 0 => null,
            0 => LastDay,
            <= 7 => Days7,
            <= 30 => Days30,
            _ => null,
        };

        /// <summary>Event payload of a threshold sent for <paramref name="lastDay"/>, before the landlord: <c>notice-t-30:2027-04-15</c>.</summary>
        public static string Payload(string threshold, DateOnly lastDay) =>
            $"{threshold}:{lastDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }
}
