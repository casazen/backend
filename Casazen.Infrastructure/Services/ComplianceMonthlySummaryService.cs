using System.Data;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceMonthlySummaryService" />
/// <remarks>
/// <list type="bullet">
/// <item><b>Which organizations.</b> Active host organizations that have a short-rent property (<see cref="PropertyRentalModeRules.IsShortRent"/>) and already
/// existed when the month ended: an organization that did not exist in that month is told nothing about it.</item>
/// <item><b>What the month is.</b> The coverage report of the proof archive for the first to the last day of the previous calendar month (Europe/Rome)
/// with the reach of the whole organization (<see cref="IComplianceArchiveService.GetCoverageReportAsync"/>): the stays, the ISTAT and tourist tax months and the
/// state of each are the traffic light's, decided by one class of rules; this service counts nothing of its own, and <b>never turns an item held only by the channel
/// manager, or one without a proof, into «compliant»</b> (<see cref="ComplianceMonthSummary.From"/>).</item>
/// <item><b>Once.</b> The row of <see cref="ComplianceMonthlySummaryLog"/> is inserted first, inside a transaction, and committed after the emails were handed to the
/// queue (<see cref="IEmailQueue"/>, delivered by a Hangfire job with retries): a second run, a retry or a concurrent run finds the row (or loses on the unique
/// index <see cref="ComplianceMonthlySummaryLog.OrgMonthIndexName"/>) and sends nothing more. When no email could be queued (no provider, no address) the row is
/// taken back so that a later run tries again. A crash between the queue and the commit sends it again rather than losing it.</item>
/// <item><b>Failures are not swallowed.</b> An organization that cannot be processed does not stop the others, but the run ends with an error once every
/// organization was tried, so that Hangfire repeats it; the repeat sends only the organizations that have no row for the month yet.</item>
/// <item><b>Who.</b> The organization's address and its administrators through <see cref="IComplianceAlertRecipientResolver"/> when it is registered, otherwise
/// the address of the organization (today's audience).</item>
/// <item><b>Privacy.</b> The email and the log hold counts, the names of actions and the month; the logs hold the organization and the month, never an address, a
/// name or a text.</item>
/// <item><b>Behind the module's flag</b> (<c>Features:ComplianceTrafficLight</c>): off, nothing is read, sent or written.</item>
/// </list>
/// </remarks>
public sealed class ComplianceMonthlySummaryService(
    AppDbContext db,
    IComplianceArchiveService archive,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IFeatureFlags featureFlags,
    ILogger<ComplianceMonthlySummaryService> logger,
    IComplianceAlertRecipientResolver? recipients = null,
    TimeProvider? timeProvider = null) : IComplianceMonthlySummaryService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ComplianceMonthlySummaryRunResult> RunAsync(DateOnly runDate, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.ComplianceTrafficLight))
        {
            logger.LogDebug("Compliance monthly summary skipped: Features:ComplianceTrafficLight is off");
            return new ComplianceMonthlySummaryRunResult(Skipped: true, Organizations: 0, Sent: 0);
        }

        // The previous calendar month of the run date, as Europe/Rome days.
        var first = new DateOnly(runDate.Year, runDate.Month, 1).AddMonths(-1);
        var last = first.AddMonths(1).AddDays(-1);
        var yearMonth = ComplianceLightRules.PeriodKey(first);
        var monthEndUtc = RomeCalendar.StartOfDayUtc(last.AddDays(1));

        // Background run: no tenant context, every org.
        var organizations = await db.Orgs
            .AsNoTracking()
            .Where(o => o.IsActive && o.OrgType == OrgType.Host && o.CreatedAt < monthEndUtc)
            .Where(o => db.Properties.Where(PropertyRentalModeRules.IsShortRent).Any(p => p.OrgId == o.Id))
            .OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.DefaultLanguage })
            .ToListAsync(ct);

        var sent = 0;
        var failures = new List<Exception>();
        foreach (var organization in organizations)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await SendAsync(organization.Id, organization.DefaultLanguage, first, last, yearMonth, ct))
                    sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One organization does not stop the others; the run fails at the end so that Hangfire runs it again, and the rows of the organizations that
                // were sent make the second run safe: it sends only what is left.
                logger.LogError(ex, "Compliance summary {YearMonth} of org {OrgId} could not be sent", yearMonth, organization.Id);
                failures.Add(ex);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        logger.LogInformation(
            "Compliance monthly summary {YearMonth}: {Organizations} organizations, {Sent} summaries queued, {Failed} failed",
            yearMonth,
            organizations.Count,
            sent,
            failures.Count);
        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} compliance summaries of {yearMonth} could not be sent", failures);

        return new ComplianceMonthlySummaryRunResult(Skipped: false, organizations.Count, sent);
    }

    /// <summary>The summary of one organization: true when it was handed to the email queue in this run.</summary>
    private async Task<bool> SendAsync(Guid orgId, string? language, DateOnly first, DateOnly last, string yearMonth, CancellationToken ct)
    {
        // The row decides; looking first spares the work of a month that was already sent.
        if (await db.ComplianceMonthlySummaryLogs.AsNoTracking().AnyAsync(l => l.OrgId == orgId && l.YearMonth == yearMonth, ct))
            return false;

        var report = await archive.GetCoverageReportAsync(new HostScope(orgId), new ComplianceReportQuery(first, last), ct);
        var summary = ComplianceMonthSummary.From(yearMonth, report);

        var addresses = await AddressesAsync(orgId, ct);
        if (addresses.Count == 0)
        {
            logger.LogWarning("Compliance summary {YearMonth} of org {OrgId} not sent: nobody to send it to", yearMonth, orgId);
            return false;
        }

        var content = EmailTemplates.ComplianceSummary(
            OrgInvitationEmails.CultureOf(language),
            summary,
            links.IsConfigured ? links.HostCompliance() : null);

        // The row and the enqueue are one step: the row goes in first, inside the transaction, and stays only if an email was queued.
        await using var transaction = await BeginTransactionAsync(ct);
        var row = new ComplianceMonthlySummaryLog
        {
            OrgId = orgId,
            YearMonth = yearMonth,
            SentAt = _clock.GetUtcNow().UtcDateTime,
            Outcome = summary.Outcome,
            RecipientCount = addresses.Count,
        };
        db.ComplianceMonthlySummaryLogs.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, ComplianceMonthlySummaryLog.OrgMonthIndexName))
        {
            // Another run sent it between the look and the insert: that row is the one.
            db.Entry(row).State = EntityState.Detached;
            logger.LogInformation("Compliance summary {YearMonth} of org {OrgId} was sent by another run", yearMonth, orgId);
            return false;
        }

        var queued = 0;
        foreach (var address in addresses)
        {
            if (emailQueue.Enqueue(address, content, EmailTemplates.Names.ComplianceMonthlySummary))
                queued++;
        }

        if (queued == 0)
        {
            // Nothing could be queued (no provider, no valid address): the row goes, so a later run tries again.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            else
            {
                db.ComplianceMonthlySummaryLogs.Remove(row);
                await db.SaveChangesAsync(ct);
            }

            logger.LogWarning("Compliance summary {YearMonth} of org {OrgId} could not be queued: no email was accepted", yearMonth, orgId);
            return false;
        }

        if (queued != addresses.Count)
        {
            row.RecipientCount = queued;
            await db.SaveChangesAsync(ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Compliance summary {YearMonth} of org {OrgId} queued for {Recipients} addresses: {Outcome}",
            yearMonth,
            orgId,
            queued,
            summary.Outcome);
        return true;
    }

    /// <summary>
    /// The addresses of the summary: the organization's address and its administrators through the port when it is registered, otherwise today's audience, the
    /// address of the organization.
    /// </summary>
    private async Task<List<string>> AddressesAsync(Guid orgId, CancellationToken ct)
    {
        if (recipients is not null)
        {
            return (await recipients.ForOrganizationAsync(orgId, ct)).Emails
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .Select(address => address.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var address = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.ContactEmail)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(address) ? [] : [address.Trim()];
    }

    /// <summary>
    /// A transaction for the insert of the row and the hand-over to the queue; <c>null</c> where there is none to begin: the provider is not relational (the tests
    /// on InMemory), or the caller already has one open and commits it.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
    }
}
