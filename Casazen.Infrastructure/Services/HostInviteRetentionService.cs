using Casazen.Core.HostInvites;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// GDPR minimization of the host invites: <c>HostInvites:RetentionDays</c> (30) days after an invite was closed, the email of the
/// invitee is no longer needed. A closed invite nobody used (revoked or expired) is deleted; a used one is kept for the
/// attribution (who invited, who accepted, when) with its email replaced by a placeholder and its message dropped. Runs whatever
/// the flags say: the retention of personal data does not depend on a feature being on. The audit trail holds no email and is kept.
/// </summary>
public sealed class HostInviteRetentionService(
    AppDbContext db,
    IOptions<HostInviteOptions> options,
    TimeProvider timeProvider,
    ILogger<HostInviteRetentionService> logger) : IHostInviteRetentionService
{
    internal const string RunLockKey = "host-invite-retention";

    /// <summary>Invites handled per step and per run: the next run takes the rest.</summary>
    private const int BatchSize = 500;

    public async Task<HostInviteRetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.HostInviteRetention, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Host invite retention skipped: another run is in progress");
            return HostInviteRetentionResult.SkippedRun;
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, options.Value.RetentionDays));

        // Closed and never used: nothing to attribute, the row goes.
        var unused = await db.HostInviteRecords
            .Where(i => !i.IsUsed && ((i.RevokedAt != null && i.RevokedAt < cutoff) || (i.RevokedAt == null && i.ExpiresAt < cutoff)))
            .OrderBy(i => i.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        db.HostInviteRecords.RemoveRange(unused);

        // Used: the attribution stays, the invitee's address and the inviter's note do not.
        var used = await db.HostInviteRecords
            .Where(i => i.IsUsed && i.PersonalDataErasedAt == null && i.UsedAt != null && i.UsedAt < cutoff)
            .OrderBy(i => i.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var invite in used)
        {
            invite.Email = HostInviteRules.ErasedEmail(invite.Id);
            invite.Message = null;
            invite.PersonalDataErasedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (unused.Count + used.Count > 0)
            logger.LogInformation("Host invite retention: {Deleted} deleted, {Erased} erased", unused.Count, used.Count);
        return new HostInviteRetentionResult(false, unused.Count, used.Count);
    }
}
