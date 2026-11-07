using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Owner invitation service implementation (ADR-011, NF-109, E10/F02/US02/T02).
/// Generates tokens, enforces rate limits, previews and confirms batches, sends emails, links Auth0 users to OwnerUser.
/// </summary>
/// <remarks>
/// <para><b>The link</b> is built by <see cref="PublicSiteLinks"/> from <c>App:PublicSiteBaseUrl</c>, the one public domain of the product (decision
/// D3), which the startup refuses unless it is an absolute https URL; there is no address in this file. It is built before anything is saved, so a
/// missing configuration leaves no invitation behind.</para>
/// <para><b>One lock.</b> The writes of the invitations of an org (a single invitation, the confirmation of a batch, a resend, a withdrawal, an
/// acceptance) take the advisory lock <c>OwnerOnboarding</c> of the org of the invitation, the one the owner import takes: two confirmations of the
/// same preview never send two invitations to the same owner, and two acceptances of the same token never both consume it.</para>
/// <para><b>E-mail.</b> It is queued after the save (the queue never throws and says whether it queued). An invitation whose e-mail was not
/// queued is withdrawn at once, so the lifecycle never says «invited» for a person nobody wrote to; the limit of the day does not count it.</para>
/// <para><b>Acceptance across orgs.</b> The person who accepts has no org, so every read of the invitation and of its ownerships names the org of the
/// invitation and ignores the tenant filter of the caller (which would be fail-closed). Only the ownerships of that org, of that registry owner, on
/// a property of that org and not over become the account's.</para>
/// </remarks>
public class OwnerInvitationService(
    AppDbContext dbContext,
    IEmailQueue emailQueue,
    ILogger<OwnerInvitationService> logger,
    PublicSiteLinks links,
    IActivityLog activityLog,
    IOwnerOnboardingService onboarding,
    IAuth0ManagementService auth0Management,
    TimeProvider timeProvider) : IOwnerInvitationService
{
    private const int MaxInvitationsPerDay = 100;
    private const int InvitationValidityDays = 7;

    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);

    // ---------------------------------------------------------------- one owner

    public async Task<Guid> CreateInvitationAsync(
        Guid orgId,
        Guid propertyOwnerId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(orgId, cancellationToken);

        // Verify PropertyOwner exists and belongs to this org, and fetch org for email template
        var propertyOwner = await dbContext.PropertyOwners
            .Include(po => po.Org)
            .FirstOrDefaultAsync(po => po.Id == propertyOwnerId && po.OrgId == orgId, cancellationToken);

        if (propertyOwner == null)
        {
            throw new NotFoundException("Property owner not found or does not belong to this organization")
            {
                Code = OwnerInvitationErrors.PropertyOwnerNotFound,
                MessageKey = "PropertyOwnerNotFound"
            };
        }

        var now = Now();
        if (await UsedTodayAsync(orgId, now, cancellationToken) >= MaxInvitationsPerDay)
        {
            throw new DomainTooManyRequestsException(
                OwnerInvitationErrors.DailyLimitReached,
                "OwnerInvitationDailyLimitReached",
                MaxInvitationsPerDay);
        }

        var invitation = await PrepareAsync(orgId, propertyOwner, actorUserId, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        if (!QueueEmail(invitation.Invitation, invitation.Token, propertyOwner, propertyOwner.Org.Name))
        {
            await WithdrawUndeliveredAsync([invitation.Invitation], actorUserId, cancellationToken);
            throw new DomainRuleException(OwnerInvitationErrors.EmailNotQueued, "OwnerInvitationEmailNotQueued");
        }

        logger.LogInformation(
            "Owner invitation created for PropertyOwner {PropertyOwnerId} in org {OrgId}",
            propertyOwnerId, orgId);

        return invitation.Invitation.Id;
    }

    // ---------------------------------------------------------------- the batch

    public async Task<OwnerInvitationPreview> PreviewBatchAsync(
        HostScope scope,
        IReadOnlyCollection<Guid>? ownerIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // An explicit list that is empty is a mistake of the caller, never «all the owners».
        if (ownerIds is { Count: 0 })
            throw new DomainRuleException(OwnerInvitationErrors.SelectionEmpty, "OwnerInvitationSelectionEmpty");

        var now = Now();
        var report = await onboarding.GetReadinessAsync(scope, cancellationToken);
        var byId = report.Owners.ToDictionary(o => o.OwnerId);
        var selection = ownerIds?.Distinct().OrderBy(id => id).ToList();

        var recipients = new List<OwnerReadinessOwner>();
        var exclusions = new List<OwnerInvitationExclusion>();
        foreach (var id in selection ?? [.. report.Owners.Select(o => o.OwnerId).OrderBy(id => id)])
        {
            // An id that is not an owner of the caller (another org's, outside the reach, unknown) is one answer, with no name.
            if (!byId.TryGetValue(id, out var owner))
            {
                exclusions.Add(new OwnerInvitationExclusion(id, null, [OwnerInvitationExclusions.OwnerNotFound]));
                continue;
            }

            if (owner.Ready.Portal && OwnerPortalStateRules.CanBeInvited(owner.PortalState))
            {
                recipients.Add(owner);
                continue;
            }

            var reasons = owner.Reasons
                .Where(r => r.Dimension == OwnerReadinessDimensions.Portal)
                .Select(r => r.Code)
                .ToList();
            if (owner.PortalState == OwnerPortalStates.Invited)
                reasons.Add(OwnerInvitationExclusions.AlreadyInvited);
            else if (owner.PortalState == OwnerPortalStates.Accepted)
                reasons.Add(OwnerInvitationExclusions.AlreadyAccepted);

            exclusions.Add(new OwnerInvitationExclusion(id, owner.DisplayName, reasons));
        }

        var ids = recipients.Select(r => r.OwnerId).ToList();
        var emails = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.PropertyOwners
                .AsNoTracking()
                .Where(o => o.OrgId == scope.OrgId && ids.Contains(o.Id))
                .Select(o => new { o.Id, o.Email })
                .ToDictionaryAsync(o => o.Id, o => o.Email.Trim(), cancellationToken);

        var rows = recipients
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.OwnerId)
            .Select(r => new OwnerInvitationRecipient(r.OwnerId, r.DisplayName, emails.GetValueOrDefault(r.OwnerId, string.Empty), r.PortalState))
            .ToList();
        var used = await UsedTodayAsync(scope.OrgId, now, cancellationToken);
        return new OwnerInvitationPreview(
            Fingerprint(scope.OrgId, selection, rows, exclusions),
            rows,
            exclusions,
            new OwnerInvitationQuota(MaxInvitationsPerDay, used, Math.Max(0, MaxInvitationsPerDay - used)));
    }

    public async Task<OwnerInvitationBatchResult> ConfirmBatchAsync(
        HostScope scope,
        string actorUserId,
        IReadOnlyCollection<Guid>? ownerIds,
        string? fingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new DomainRuleException(OwnerInvitationErrors.PreviewRequired, "OwnerInvitationPreviewRequired");

        await using var transaction = await LockAsync(scope.OrgId, cancellationToken);

        // Judged again, under the lock: what the person saw must still be true, or nothing is sent.
        var preview = await PreviewBatchAsync(scope, ownerIds, cancellationToken);
        if (!FingerprintsMatch(preview.Fingerprint, fingerprint.Trim()))
            throw new DomainConflictException(OwnerInvitationErrors.PreviewStale, "OwnerInvitationPreviewStale");

        if (preview.Recipients.Count == 0)
            throw new DomainRuleException(OwnerInvitationErrors.NothingToSend, "OwnerInvitationNothingToSend");

        // All or nothing as to the limit of the day: a batch that does not fit is not cut.
        if (preview.Recipients.Count > preview.Quota.Remaining)
        {
            throw new DomainTooManyRequestsException(
                OwnerInvitationErrors.DailyLimitReached,
                "OwnerInvitationDailyLimitReached",
                MaxInvitationsPerDay);
        }

        var now = Now();
        var recipientIds = preview.Recipients.Select(r => r.OwnerId).ToList();
        var owners = await dbContext.PropertyOwners
            .Where(o => o.OrgId == scope.OrgId && recipientIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, cancellationToken);
        var agencyName = await AgencyNameAsync(scope.OrgId, cancellationToken);

        var prepared = new List<(OwnerInvitation Invitation, string Token, PropertyOwner Owner)>(recipientIds.Count);
        foreach (var recipient in preview.Recipients)
        {
            var owner = owners[recipient.OwnerId];
            var one = await PrepareAsync(scope.OrgId, owner, actorUserId, now, cancellationToken);
            prepared.Add((one.Invitation, one.Token, owner));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var failed = new List<OwnerInvitationFailure>();
        var undelivered = new List<OwnerInvitation>();
        foreach (var (invitation, token, owner) in prepared)
        {
            if (QueueEmail(invitation, token, owner, agencyName))
                continue;

            undelivered.Add(invitation);
            failed.Add(new OwnerInvitationFailure(owner.Id, OwnerInvitationErrors.EmailNotQueued));
        }

        if (undelivered.Count > 0)
            await WithdrawUndeliveredAsync(undelivered, actorUserId, cancellationToken);

        var sent = prepared
            .Where(p => !undelivered.Contains(p.Invitation))
            .Select(p => new OwnerInvitationSent(p.Invitation.Id, p.Owner.Id, p.Invitation.ExpiresAt))
            .ToList();

        // Counts only: no name, no address, no link.
        logger.LogInformation(
            "Owner invitations of org {OrgId}: {Sent} sent, {Failed} not queued",
            scope.OrgId,
            sent.Count,
            failed.Count);
        return new OwnerInvitationBatchResult(sent.Count, sent, failed);
    }

    public async Task<IReadOnlyList<OwnerInvitationStatusItem>> ListAsync(HostScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var report = await onboarding.GetReadinessAsync(scope, cancellationToken);
        var invitations = (await dbContext.OwnerInvitations
                .AsNoTracking()
                .Where(i => i.OrgId == scope.OrgId)
                .Select(i => new { i.Id, i.PropertyOwnerId, i.Status, i.ExpiresAt, i.CreatedAt, i.AcceptedAt, i.ClosedAt })
                .ToListAsync(cancellationToken))
            .ToLookup(i => i.PropertyOwnerId);

        return
        [
            .. report.Owners.Select(owner =>
            {
                // The accepted invitation says it all; otherwise the latest one.
                var own = invitations[owner.OwnerId].ToList();
                var shown = own.FirstOrDefault(i => i.Status == OwnerInvitationStatus.Accepted)
                    ?? own.OrderByDescending(i => i.CreatedAt).FirstOrDefault();
                return new OwnerInvitationStatusItem(
                    owner.OwnerId,
                    owner.DisplayName,
                    owner.PortalState,
                    shown?.Id,
                    shown?.CreatedAt,
                    shown?.ExpiresAt,
                    shown?.AcceptedAt,
                    shown?.ClosedAt);
            }),
        ];
    }

    public async Task<OwnerInvitationStatusItem> ResendAsync(
        HostScope scope,
        string actorUserId,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        await using var transaction = await LockAsync(scope.OrgId, cancellationToken);
        var invitation = await FindVisibleAsync(scope, invitationId, cancellationToken);

        // The owner has accepted (this invitation or an earlier one): there is no link to send again.
        if (await dbContext.OwnerInvitations.AnyAsync(
                i => i.OrgId == scope.OrgId && i.PropertyOwnerId == invitation.PropertyOwnerId && i.Status == OwnerInvitationStatus.Accepted,
                cancellationToken))
        {
            throw new DomainConflictException(OwnerInvitationErrors.Used, "OwnerInvitationUsed");
        }

        var owner = await dbContext.PropertyOwners.Include(o => o.Org).FirstAsync(o => o.Id == invitation.PropertyOwnerId, cancellationToken);
        var now = Now();
        if (await UsedTodayAsync(scope.OrgId, now, cancellationToken) >= MaxInvitationsPerDay)
        {
            throw new DomainTooManyRequestsException(
                OwnerInvitationErrors.DailyLimitReached,
                "OwnerInvitationDailyLimitReached",
                MaxInvitationsPerDay);
        }

        var created = await PrepareAsync(scope.OrgId, owner, actorUserId, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        if (!QueueEmail(created.Invitation, created.Token, owner, owner.Org.Name))
        {
            await WithdrawUndeliveredAsync([created.Invitation], actorUserId, cancellationToken);
            throw new DomainRuleException(OwnerInvitationErrors.EmailNotQueued, "OwnerInvitationEmailNotQueued");
        }

        return await ItemAsync(scope.OrgId, owner, now, cancellationToken);
    }

    public async Task<OwnerInvitationStatusItem> RevokeAsync(
        HostScope scope,
        string actorUserId,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        await using var transaction = await LockAsync(scope.OrgId, cancellationToken);
        var invitation = await FindVisibleAsync(scope, invitationId, cancellationToken);
        var now = Now();

        switch (invitation.Status)
        {
            case OwnerInvitationStatus.Accepted:
                throw new DomainConflictException(OwnerInvitationErrors.Used, "OwnerInvitationUsed");
            case OwnerInvitationStatus.Expired:
                throw new DomainConflictException(OwnerInvitationErrors.NotLive, "OwnerInvitationNotLive");
            case OwnerInvitationStatus.Pending:
                invitation.Status = OwnerInvitationStatus.Revoked;
                invitation.ClosedAt = now;
                invitation.UpdatedAt = now;
                activityLog.Record(Line(invitation, actorUserId, "revoked"));
                await dbContext.SaveChangesAsync(cancellationToken);
                break;
        }

        // A second withdrawal finds it done: nothing written, no second line.
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var owner = await dbContext.PropertyOwners.AsNoTracking().FirstAsync(o => o.Id == invitation.PropertyOwnerId, cancellationToken);
        return await ItemAsync(scope.OrgId, owner, now, cancellationToken);
    }

    // ---------------------------------------------------------------- the acceptance

    public async Task<OwnerInvitationAccepted> AcceptAsync(AcceptOwnerInvitation command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Auth0Sub);

        // Normalize token
        if (!TryNormalizeToken(command.Token, out var normalizedToken))
            throw new DomainGoneException(OwnerInvitationErrors.Invalid, "OwnerInvitationInvalid");

        var tokenHash = HashToken(normalizedToken);

        // The caller has no org: this lookup (and every one below) names the org of the invitation and ignores the tenant filter of the caller.
        var orgId = await dbContext.OwnerInvitations
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(i => i.TokenHash == tokenHash)
            .Select(i => (Guid?)i.OrgId)
            .FirstOrDefaultAsync(cancellationToken);
        if (orgId is null)
            throw new DomainGoneException(OwnerInvitationErrors.Invalid, "OwnerInvitationInvalid");

        await using var transaction = await LockAsync(orgId.Value, cancellationToken);

        // Read again, under the lock: a second acceptance of the same token waits and finds what the first did.
        var invitation = await dbContext.OwnerInvitations
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash && i.OrgId == orgId.Value, cancellationToken);
        if (invitation is null || !TokenMatches(invitation.TokenHash, normalizedToken))
            throw new DomainGoneException(OwnerInvitationErrors.Invalid, "OwnerInvitationInvalid");

        var now = Now();
        var existingUser = await dbContext.OwnerUsers.FirstOrDefaultAsync(ou => ou.Auth0Sub == command.Auth0Sub, cancellationToken);

        // Check invitation status
        if (invitation.Status == OwnerInvitationStatus.Accepted)
        {
            // Idempotent for the account that accepted it, and for nobody else: the token is one-time use.
            if (existingUser is not null && invitation.OwnerUserId == existingUser.Id)
            {
                logger.LogInformation(
                    "Invitation {InvitationId} already accepted by OwnerUser {OwnerUserId}: same answer",
                    invitation.Id, existingUser.Id);
                return await ResultAsync(command, existingUser.Id, alreadyAccepted: true, now, cancellationToken);
            }

            throw new DomainGoneException(OwnerInvitationErrors.Used, "OwnerInvitationUsed");
        }

        if (invitation.Status == OwnerInvitationStatus.Revoked)
            throw new DomainGoneException(OwnerInvitationErrors.Revoked, "OwnerInvitationRevoked");

        if (invitation.Status == OwnerInvitationStatus.Expired || invitation.ExpiresAt <= now)
            throw new DomainGoneException(OwnerInvitationErrors.Expired, "OwnerInvitationExpired");

        // The e-mail comes from the token or from Auth0 and has to be verified: a link in the hands of somebody else is worth nothing.
        if (!command.EmailVerified)
            throw new DomainForbiddenException(OwnerInvitationErrors.EmailNotVerified, "OwnerInvitationEmailNotVerified");

        var normalizedEmail = (command.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.Equals(invitation.Email, normalizedEmail, StringComparison.Ordinal))
            throw new DomainForbiddenException(OwnerInvitationErrors.EmailMismatch, "OwnerInvitationEmailMismatch");

        // Find or create OwnerUser
        var ownerUser = existingUser;
        if (ownerUser is null)
        {
            ownerUser = new OwnerUser
            {
                Auth0Sub = command.Auth0Sub,
                Email = normalizedEmail,
                FirstName = command.FirstName,
                LastName = command.LastName,
                CreatedAt = now,
            };
            dbContext.OwnerUsers.Add(ownerUser);
        }

        ownerUser.LastLoginAt = now;

        // Only the ownerships of this invitation: this org, this registry owner, a property of this org, not over. Never one already given to
        // somebody else, never another org's.
        var propertyIds = dbContext.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(p => p.OrgId == orgId.Value)
            .Select(p => p.Id);
        var ownerships = await dbContext.PropertyOwnerships
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(po => po.OrgId == orgId.Value
                         && po.PropertyOwnerId == invitation.PropertyOwnerId
                         && (po.OwnershipEnd == null || po.OwnershipEnd >= now)
                         && propertyIds.Contains(po.PropertyId))
            .ToListAsync(cancellationToken);
        foreach (var ownership in ownerships.Where(o => o.OwnerUserId is null))
            ownership.OwnerUserId = ownerUser.Id;

        // Update invitation
        invitation.Status = OwnerInvitationStatus.Accepted;
        invitation.AcceptedAt = now;
        invitation.OwnerUserId = ownerUser.Id;
        invitation.ClosedAt = now;
        invitation.UpdatedAt = now;
        activityLog.Record(Line(invitation, command.Auth0Sub, "accepted"));

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Invitation {InvitationId} accepted by OwnerUser {OwnerUserId} for PropertyOwner {PropertyOwnerId}",
            invitation.Id, ownerUser.Id, invitation.PropertyOwnerId);

        return await ResultAsync(command, ownerUser.Id, alreadyAccepted: false, now, cancellationToken);
    }

    /// <summary>
    /// The claims of the owner as they stand after the acceptance (every org, every ownership of the account that is not over) and their hand-over
    /// to the identity provider. The hand-over is repeated by every acceptance of the same token, so a failure is cured by accepting again.
    /// </summary>
    private async Task<OwnerInvitationAccepted> ResultAsync(
        AcceptOwnerInvitation command,
        Guid ownerUserId,
        bool alreadyAccepted,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = (await dbContext.Properties
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Join(
                    dbContext.PropertyOwnerships
                        .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                        .AsNoTracking()
                        .Where(po => po.OwnerUserId == ownerUserId && (po.OwnershipEnd == null || po.OwnershipEnd >= now)),
                    property => property.Id,
                    ownership => ownership.PropertyId,
                    (property, ownership) => new { PropertyId = property.Id, PropertyOrgId = property.OrgId, ownership.OrgId })
                .ToListAsync(cancellationToken))
            .Where(r => r.PropertyOrgId == r.OrgId)
            .ToList();
        var agencyIds = existing.Select(r => r.OrgId).Distinct().Order().ToList();
        var propertyIds = existing.Select(r => r.PropertyId).Distinct().Order().ToList();

        var sync = await auth0Management.SetOwnerPortalClaimsAsync(command.Auth0Sub, ownerUserId, agencyIds, propertyIds, cancellationToken);
        var refresh = sync.Status switch
        {
            Auth0SyncStatus.Synced => OwnerClaimsRefresh.Published,
            Auth0SyncStatus.NotConfigured => OwnerClaimsRefresh.NotConfigured,
            _ => OwnerClaimsRefresh.Failed,
        };
        return new OwnerInvitationAccepted(ownerUserId, agencyIds, propertyIds, alreadyAccepted, refresh);
    }

    // ---------------------------------------------------------------- shared

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> LockAsync(Guid orgId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            dbContext, cancellationToken, (PostgresAdvisoryLocks.Scope.OwnerOnboarding, orgId.ToString("N")));

    /// <summary>How many invitations the org sent in the last 24 hours: the instants of every send kept on its invitations.</summary>
    private async Task<int> UsedTodayAsync(Guid orgId, DateTime now, CancellationToken cancellationToken)
    {
        var since = now.AddDays(-1);
        var sendLogs = await dbContext.OwnerInvitations
            .AsNoTracking()
            .Where(i => i.OrgId == orgId)
            .Select(i => i.SendLog)
            .ToListAsync(cancellationToken);
        return sendLogs.SelectMany(log => log).Count(d => d > since);
    }

    private async Task<string> AgencyNameAsync(Guid orgId, CancellationToken cancellationToken) =>
        await dbContext.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

    /// <summary>
    /// A new invitation for the owner, staged with its line of the log, and the link in force withdrawn (a link that lapsed is closed as expired). Not
    /// saved. The link is built first, so a missing public address stops here with nothing staged.
    /// </summary>
    private async Task<(OwnerInvitation Invitation, string Token)> PrepareAsync(
        Guid orgId,
        PropertyOwner owner,
        string actorUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var token = GenerateToken();
        _ = links.OwnerInvitationAccept(token);

        var open = await dbContext.OwnerInvitations
            .Where(i => i.OrgId == orgId && i.PropertyOwnerId == owner.Id && i.Status == OwnerInvitationStatus.Pending)
            .ToListAsync(cancellationToken);
        var replaced = false;
        foreach (var previous in open)
        {
            if (previous.ExpiresAt > now)
            {
                previous.Status = OwnerInvitationStatus.Revoked;
                replaced = true;
                activityLog.Record(Line(previous, actorUserId, "revoked"));
            }
            else
            {
                previous.Status = OwnerInvitationStatus.Expired;
            }

            previous.ClosedAt = now;
            previous.UpdatedAt = now;
        }

        var invitation = new OwnerInvitation
        {
            OrgId = orgId,
            PropertyOwnerId = owner.Id,
            Email = owner.Email.Trim().ToLowerInvariant(),
            TokenHash = HashToken(token),
            Status = OwnerInvitationStatus.Pending,
            ExpiresAt = now.AddDays(InvitationValidityDays),
            SendLog = [now],
            InvitedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Through the DbSet: the Guid of the row is preset, so a row attached any other way would be tracked as modified.
        dbContext.OwnerInvitations.Add(invitation);
        activityLog.Record(Line(invitation, actorUserId, replaced ? "resent" : "sent"));
        return (invitation, token);
    }

    /// <summary>Queues the e-mail of an invitation; false when it could not be queued (the provider is not configured, no address).</summary>
    private bool QueueEmail(OwnerInvitation invitation, string token, PropertyOwner owner, string agencyName)
    {
        var content = EmailTemplates.OwnerInvitation(
            new CultureInfo("it-IT"),
            OwnerFieldRules.DisplayName(owner),
            agencyName,
            links.OwnerInvitationAccept(token),
            invitation.ExpiresAt);
        return emailQueue.Enqueue(owner.Email, content, EmailTemplates.Names.OwnerInvitation);
    }

    /// <summary>
    /// Withdraws the invitations whose e-mail was not queued: they said «invited» for a person nobody wrote to. The send leaves the count of the day.
    /// </summary>
    private async Task WithdrawUndeliveredAsync(IReadOnlyCollection<OwnerInvitation> invitations, string actorUserId, CancellationToken cancellationToken)
    {
        var now = Now();
        foreach (var invitation in invitations)
        {
            var stored = await dbContext.OwnerInvitations.FirstAsync(i => i.Id == invitation.Id, cancellationToken);
            stored.Status = OwnerInvitationStatus.Revoked;
            stored.ClosedAt = now;
            stored.UpdatedAt = now;
            stored.SendLog = [];
            activityLog.Record(Line(stored, actorUserId, "revoked"));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogWarning("{Count} owner invitations withdrawn: the e-mail could not be queued", invitations.Count);
    }

    private async Task<OwnerInvitation> FindVisibleAsync(HostScope scope, Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await dbContext.OwnerInvitations.FirstOrDefaultAsync(i => i.Id == invitationId && i.OrgId == scope.OrgId, cancellationToken);
        if (invitation is null || !await OwnerVisibleAsync(scope, invitation.PropertyOwnerId, cancellationToken))
            throw new NotFoundException("Owner invitation not found.") { Code = OwnerInvitationErrors.NotFound, MessageKey = "OwnerInvitationNotFound" };

        return invitation;
    }

    /// <summary>An owner of the org not anonymized, and, for a caller with a partial reach, one that holds a period on a property it reaches.</summary>
    private async Task<bool> OwnerVisibleAsync(HostScope scope, Guid ownerId, CancellationToken cancellationToken)
    {
        if (!await dbContext.PropertyOwners.AnyAsync(o => o.Id == ownerId && o.OrgId == scope.OrgId && o.AnonymizedAt == null, cancellationToken))
            return false;

        if (scope.IsOrgWide)
            return true;

        return await dbContext.PropertyOwnerships
            .Where(po => po.OrgId == scope.OrgId && po.PropertyOwnerId == ownerId)
            .InScope(scope, po => po.Property)
            .AnyAsync(cancellationToken);
    }

    private async Task<OwnerInvitationStatusItem> ItemAsync(Guid orgId, PropertyOwner owner, DateTime now, CancellationToken cancellationToken)
    {
        var own = await dbContext.OwnerInvitations
            .AsNoTracking()
            .Where(i => i.OrgId == orgId && i.PropertyOwnerId == owner.Id)
            .ToListAsync(cancellationToken);
        var shown = own.FirstOrDefault(i => i.Status == OwnerInvitationStatus.Accepted) ?? own.OrderByDescending(i => i.CreatedAt).FirstOrDefault();
        var state = OwnerPortalStateRules.StateOf(
            [.. own.Select(i => new OwnerInvitationFact(i.Id, i.Status, i.ExpiresAt, i.CreatedAt))],
            now,
            portalReady: true);
        return new OwnerInvitationStatusItem(
            owner.Id,
            OwnerFieldRules.DisplayName(owner),
            state,
            shown?.Id,
            shown?.CreatedAt,
            shown?.ExpiresAt,
            shown?.AcceptedAt,
            shown?.ClosedAt);
    }

    private static OrgActivity Line(OwnerInvitation invitation, string actorUserId, string status) =>
        OrgActivity.Of(
            invitation.OrgId,
            OrgActivityType.OwnerInvitationChanged,
            actorUserId,
            invitation.Id.ToString("D"),
            (OrgActivityDetailKeys.Status, status));

    /// <summary>
    /// The fingerprint of a preview: SHA-256 of the org, the selection, each recipient with its address (lower case) and state, and each exclusion
    /// with its reasons, in a fixed order. It says nothing of the data and changes with any of it.
    /// </summary>
    private static string Fingerprint(
        Guid orgId,
        IReadOnlyList<Guid>? selection,
        IReadOnlyList<OwnerInvitationRecipient> recipients,
        IReadOnlyList<OwnerInvitationExclusion> exclusions)
    {
        var text = new StringBuilder();
        text.Append("cz-owner-invitation-preview-v1|").Append(orgId.ToString("N")).Append('|');
        text.Append(selection is null ? "all" : string.Join(',', selection.Select(id => id.ToString("N")))).Append('\n');
        foreach (var recipient in recipients.OrderBy(r => r.OwnerId))
        {
            text.Append("r|").Append(recipient.OwnerId.ToString("N")).Append('|')
                .Append(recipient.Email.Trim().ToLowerInvariant()).Append('|').Append(recipient.PortalState).Append('\n');
        }

        foreach (var exclusion in exclusions.OrderBy(x => x.OwnerId))
        {
            text.Append("x|").Append(exclusion.OwnerId.ToString("N")).Append('|')
                .Append(string.Join(',', exclusion.Reasons.Order(StringComparer.Ordinal))).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static bool FingerprintsMatch(string expected, string given) =>
        expected.Length == given.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given.ToLowerInvariant()));

    private static string GenerateToken()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static bool TryNormalizeToken(string? value, out string token)
    {
        token = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return token.Length == 64 && token.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    private static bool TokenMatches(string storedHash, string token)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        var computedHash = HashToken(token);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(computedHash));
    }
}
