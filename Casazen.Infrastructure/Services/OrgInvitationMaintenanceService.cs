using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgInvitationMaintenanceService" />
/// <remarks>
/// <list type="bullet">
/// <item><b>Expiry.</b> A pending invitation past its expiry becomes <see cref="OrgInvitationStatus.Expired"/> and the
/// person who invited is told once (only the run that changes the status sends the note). The seat was already free: the
/// seat count excludes an invitation by its date, whether or not this job has run.</item>
/// <item><b>Reminder.</b> Three days after an invitation was sent and until it expires, the invited person gets one
/// reminder with a <b>fresh link</b> (the token is rotated: only the hash is stored). The email is queued first and the new
/// hash saved only if it was queued, so a failure never leaves a person with a dead link and no email; the invitation is
/// simply tried again at the next run. Only with the <c>OrgTeam</c> flag on: with it off the screens and the endpoints
/// do not exist and nobody should receive mail about them.</item>
/// <item><b>Purge.</b> A closed invitation (accepted, revoked, expired) is deleted, with the name and the email of the
/// invitee, <c>OrgTeam:InvitationRetentionDays</c> (30) days after it was closed. This runs with the flag off too: the
/// retention of personal data does not depend on a feature being on.</item>
/// </list>
/// <para>Every invitation is handled in a transaction of its own under its org's seats lock, so the job never races with a
/// person who accepts, revokes or sends it again, and one failing invitation does not stop the others.
/// A session advisory lock keeps two runs from overlapping (on top of Hangfire's own lock).</para>
/// </remarks>
public sealed class OrgInvitationMaintenanceService(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IConfiguration configuration,
    ILogger<OrgInvitationMaintenanceService> logger,
    TimeProvider? timeProvider = null) : IOrgInvitationMaintenanceService
{
    /// <summary>The key of the session lock of a run (<see cref="PostgresAdvisoryLocks.Scope.OrgInvitationMaintenance"/>).</summary>
    internal const string RunLockKey = "org-invitation-maintenance";

    /// <summary>Invitations handled per step and per run: the next run takes the rest.</summary>
    private const int BatchSize = 200;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OrgInvitationMaintenanceResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.OrgInvitationMaintenance, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Org invitation maintenance skipped: another run is in progress");
            return OrgInvitationMaintenanceResult.SkippedRun;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var notify = featureFlags.IsEnabled(FeatureFlags.OrgTeam);

        var expired = await ExpireAsync(now, notify, cancellationToken);
        var reminded = notify ? await RemindAsync(now, cancellationToken) : 0;
        var purged = await PurgeAsync(now, cancellationToken);

        if (expired + reminded + purged > 0)
        {
            logger.LogInformation(
                "Org invitation maintenance: {Reminded} reminded, {Expired} expired, {Purged} deleted",
                reminded, expired, purged);
        }

        return new OrgInvitationMaintenanceResult(false, reminded, expired, purged);
    }

    // ─── Expiry ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> ExpireAsync(DateTime now, bool notify, CancellationToken cancellationToken)
    {
        var due = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Status == OrgInvitationStatus.Pending && i.ExpiresAt <= now)
            .OrderBy(i => i.ExpiresAt)
            .Select(i => new { i.Id, i.OrgId })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var invitation = await ExpireOneAsync(item.Id, item.OrgId, now, cancellationToken);
                if (invitation is null)
                    continue;

                expired++;
                if (notify)
                    await NotifyInviterAsync(invitation, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Org invitation {InvitationId} could not be expired", item.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return expired;
    }

    /// <summary>The invitation, expired now; <c>null</c> when it is no longer pending (accepted, revoked or sent again meanwhile).</summary>
    private async Task<OrgInvitation?> ExpireOneAsync(Guid id, Guid orgId, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken);

        var invitation = await db.OrgInvitations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                i => i.Id == id && i.Status == OrgInvitationStatus.Pending && i.ExpiresAt <= now,
                cancellationToken);
        if (invitation is null)
            return null;

        invitation.Status = OrgInvitationStatus.Expired;
        invitation.ClosedAt ??= invitation.ExpiresAt;
        invitation.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return invitation;
    }

    /// <summary>
    /// Tells the person who invited that nobody accepted (at most once: only the run that expired it gets here). The inviter
    /// if it still manages the org, the owner otherwise; nobody when there is no one to tell.
    /// </summary>
    private async Task NotifyInviterAsync(OrgInvitation invitation, CancellationToken cancellationToken)
    {
        var managers = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.OrgId == invitation.OrgId
                        && m.Status == OrgMemberStatus.Active
                        && (m.Role == OrgRole.Owner || m.Role == OrgRole.Admin))
            .Select(m => new { m.UserId, m.Role })
            .ToListAsync(cancellationToken);

        var recipientId = managers.Any(m => m.UserId == invitation.InvitedByUserId)
            ? invitation.InvitedByUserId
            : managers.FirstOrDefault(m => m.Role == OrgRole.Owner)?.UserId;
        if (recipientId is null)
            return;

        var recipient = await db.Users.AsNoTracking()
            .Where(u => u.Id == recipientId)
            .Select(u => new { u.Email, u.FirstName, u.LastName })
            .FirstOrDefaultAsync(cancellationToken);
        if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email))
            return;

        var name = $"{recipient.FirstName} {recipient.LastName}".Trim();
        var content = OrgInvitationEmails.Expired(publicSiteLinks, invitation, string.IsNullOrWhiteSpace(name) ? recipient.Email : name);
        if (!emailQueue.Enqueue(recipient.Email, content, EmailTemplates.Names.OrgInvitationExpired))
            logger.LogWarning("The expiry note of org invitation {InvitationId} was not queued", invitation.Id);
    }

    // ─── Reminder ───────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> RemindAsync(DateTime now, CancellationToken cancellationToken)
    {
        // Three days after it was sent = four days before it expires (a week of validity).
        var dueUntil = now + (OrgInvitationRules.Validity - OrgInvitationRules.ReminderAfter);
        var due = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Status == OrgInvitationStatus.Pending
                        && i.ExpiresAt > now
                        && i.ReminderSentAt == null
                        && i.ExpiresAt <= dueUntil)
            .OrderBy(i => i.ExpiresAt)
            .Select(i => new { i.Id, i.OrgId })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var reminded = 0;
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await RemindOneAsync(item.Id, item.OrgId, now, dueUntil, cancellationToken))
                    reminded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Org invitation {InvitationId} could not be reminded", item.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return reminded;
    }

    private async Task<bool> RemindOneAsync(Guid id, Guid orgId, DateTime now, DateTime dueUntil, CancellationToken cancellationToken)
    {
        await using var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken);

        var invitation = await db.OrgInvitations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                i => i.Id == id
                     && i.Status == OrgInvitationStatus.Pending
                     && i.ExpiresAt > now
                     && i.ReminderSentAt == null
                     && i.ExpiresAt <= dueUntil,
                cancellationToken);
        if (invitation is null)
            return false;

        var org = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId && o.IsActive)
            .Select(o => new { o.DisplayName, o.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (org is null)
            return false;

        // The link of the first email stops working with the new token: queue the email first, rotate only if it is queued.
        var token = OrgInvitationTokens.Generate();
        var content = OrgInvitationEmails.Reminder(
            publicSiteLinks, invitation, token, string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName);
        if (!emailQueue.Enqueue(invitation.Email, content, EmailTemplates.Names.OrgInvitationReminder))
        {
            logger.LogWarning("The reminder of org invitation {InvitationId} was not queued: tried again at the next run", invitation.Id);
            return false;
        }

        invitation.TokenHash = OrgInvitationTokens.Hash(token);
        invitation.ReminderSentAt = now;
        invitation.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return true;
    }

    // ─── Retention ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> PurgeAsync(DateTime now, CancellationToken cancellationToken)
    {
        var retentionDays = Math.Max(1, configuration.GetValue(OrgInvitationRules.RetentionDaysConfigKey, OrgInvitationRules.DefaultRetentionDays));
        var cutoff = now.AddDays(-retentionDays);

        var orgIds = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Status != OrgInvitationStatus.Pending && i.ClosedAt != null && i.ClosedAt < cutoff)
            .Select(i => i.OrgId)
            .Distinct()
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var purged = 0;
        foreach (var orgId in orgIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                purged += await PurgeOrgAsync(orgId, cutoff, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The closed invitations of org {OrgId} could not be deleted", orgId);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return purged;
    }

    /// <summary>Deletes the org's closed invitations older than the cutoff, under its seats lock (a send-again of an old one cannot meet the delete).</summary>
    private async Task<int> PurgeOrgAsync(Guid orgId, DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken);

        var old = await db.OrgInvitations.IgnoreQueryFilters()
            .Where(i => i.OrgId == orgId
                        && i.Status != OrgInvitationStatus.Pending
                        && i.ClosedAt != null
                        && i.ClosedAt < cutoff)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (old.Count == 0)
            return 0;

        db.OrgInvitations.RemoveRange(old);
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return old.Count;
    }
}
