using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.HostInvites;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Invitation of new independent hosts (see <see cref="IHostInviteService"/>). The invite grants nothing: it binds an email,
/// leads to the normal signup and records who invited.
/// </summary>
/// <remarks>
/// <para><b>Account enumeration.</b> A host must not be able to find out whether an email is registered. When the invited email
/// already belongs to a user, a host's invite is created and answered exactly like any other (same status, same body, same
/// quota use, same row in its list), and simply no email is sent: the registered person is not mailed and the host learns
/// nothing. A platform admin, a trusted operator, gets the explicit 409 <c>host_email_taken</c>. Duplicate invites are
/// checked per inviter, so the answer never tells that someone else invited the address.</para>
/// <para><b>Limits.</b> The daily quota (24 hours, rolling) is per host org and counts creations and resends from the audit
/// trail; the pending cap is per host org. Both are decided under an advisory lock, so parallel requests cannot overshoot.
/// Platform admins have neither.</para>
/// </remarks>
public sealed class HostInviteService(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IOptions<HostInviteOptions> options,
    TimeProvider timeProvider,
    ILogger<HostInviteService> logger) : IHostInviteService
{
    public async Task<HostInviteSent> CreateAsync(CreateHostInvite request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = RequireActor(request.Actor);

        var email = HostInviteRules.NormalizeEmail(request.Email);
        if (email.Length == 0 || email.Length > HostInviteRecord.EmailMaxLength || !email.Contains('@', StringComparison.Ordinal))
            throw new DomainRuleException(HostInviteErrors.EmailInvalid, HostInviteErrors.EmailInvalidMessageKey);

        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        if (message is { Length: > HostInviteRecord.MessageMaxLength })
            message = message[..HostInviteRecord.MessageMaxLength];

        var inviter = await db.Users.AsNoTracking()
            .Where(u => u.Id == actor.UserId)
            .Select(u => new { u.Email, u.FirstName, u.LastName })
            .FirstOrDefaultAsync(cancellationToken);
        if (inviter is not null && HostInviteRules.NormalizeEmail(inviter.Email) == email)
            throw new DomainRuleException(HostInviteErrors.Self, HostInviteErrors.SelfMessageKey);

        var now = Now();
        var token = SupplierInviteTokens.Generate();
        var invite = new HostInviteRecord
        {
            Email = email,
            TokenHash = SupplierInviteTokens.Hash(token),
            Locale = HostInviteRules.NormalizeLocale(request.Locale),
            Message = message,
            InvitedByUserId = actor.UserId,
            InvitedByOrgId = actor.IsAdmin ? null : actor.OrgId,
            CreatedAt = now,
            ExpiresAt = now.Add(HostInviteRules.Validity),
        };

        // Rendered before saving: a missing App:PublicSiteBaseUrl is a configuration error, not an invite with a wrong link.
        var inviterName = actor.IsAdmin ? null : await InviterDisplayNameAsync(actor, inviter?.FirstName, inviter?.LastName, cancellationToken);
        var content = BuildEmail(invite, token, inviterName);

        bool sendEmail;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, (PostgresAdvisoryLocks.Scope.HostInviteQuota, LockKey(actor))))
        {
            if (!actor.IsAdmin)
            {
                await EnsureQuotaAsync(actor.OrgId!.Value, now, cancellationToken);
                await EnsurePendingRoomAsync(actor.OrgId.Value, now, cancellationToken);
            }

            if (await HasPendingFromAsync(actor.UserId, email, null, now, cancellationToken))
                throw new DomainConflictException(HostInviteErrors.Duplicate, HostInviteErrors.DuplicateMessageKey);

            sendEmail = await EmailAllowsSendingAsync(actor, email, cancellationToken);

            db.HostInviteRecords.Add(invite);
            db.HostInviteAuditEntries.Add(Audit(invite.Id, HostInviteAuditAction.Created, actor, now));
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        if (sendEmail && !emailQueue.Enqueue(invite.Email, content, EmailTemplates.Names.HostInvite))
            logger.LogWarning("Host invite {InviteId} created but its email was not queued", invite.Id);

        logger.LogInformation(
            "Host invite {InviteId} created by {ActorUserId} (org {ActorOrgId}, admin {IsAdmin}) for {MaskedEmail}, expires {ExpiresAt}, emailed {Emailed}",
            invite.Id, actor.UserId, actor.OrgId, actor.IsAdmin, LogRedaction.MaskEmail(email), invite.ExpiresAt, sendEmail);
        return new HostInviteSent(invite.Id, invite.ExpiresAt);
    }

    public async Task<(IReadOnlyList<HostInviteItem> Items, int Total)> ListAsync(
        HostInviteListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var actor = RequireActor(query.Actor);
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var now = Now();

        var invites = db.HostInviteRecords.AsNoTracking().AsQueryable();
        if (!actor.IsAdmin)
        {
            var userId = actor.UserId;
            invites = invites.Where(i => i.InvitedByUserId == userId);
        }

        if (query.State is { } state)
            invites = invites.Where(HostInviteRules.StatePredicate(state, now));

        if (SupplierAdminService.LikePattern(query.Search) is { } pattern)
            invites = invites.Where(i => EF.Functions.ILike(i.Email, pattern, SupplierAdminService.LikeEscape));

        var total = await invites.CountAsync(cancellationToken);
        var rows = await invites
            .OrderByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // Who invited is shown to the admin only: a host sees its own invites, it knows who invited.
        var inviters = actor.IsAdmin ? await InvitersAsync(rows, cancellationToken) : [];
        return (rows.Select(i => ToItem(i, now, inviters.GetValueOrDefault(i.Id))).ToList(), total);
    }

    public async Task<HostInviteSent> ResendAsync(HostInviteActor actorInfo, Guid inviteId, CancellationToken cancellationToken = default)
    {
        var actor = RequireActor(actorInfo);
        var now = Now();

        var invite = await FindForActorAsync(actor, inviteId, cancellationToken);
        if (invite.IsUsed || invite.RevokedAt is not null)
            throw new DomainConflictException(HostInviteErrors.NotResendable, HostInviteErrors.NotResendableMessageKey);

        var inviter = await db.Users.AsNoTracking()
            .Where(u => u.Id == invite.InvitedByUserId)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstOrDefaultAsync(cancellationToken);
        var inviterActor = new HostInviteActor(invite.InvitedByUserId, invite.InvitedByOrgId, invite.InvitedByOrgId is null);
        var inviterName = invite.InvitedByOrgId is null
            ? null
            : await InviterDisplayNameAsync(inviterActor, inviter?.FirstName, inviter?.LastName, cancellationToken);

        var token = SupplierInviteTokens.Generate();
        var tokenHash = SupplierInviteTokens.Hash(token);
        var previouslyPending = HostInviteRules.StateOf(invite, now) == HostInviteState.Pending;

        // The new link is rendered before anything changes (a missing public URL is a configuration error).
        var expiresAt = now.Add(HostInviteRules.Validity);
        var content = BuildEmail(invite, token, inviterName, expiresAt);

        bool sendEmail;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, (PostgresAdvisoryLocks.Scope.HostInviteQuota, LockKey(invite.InvitedByOrgId is null ? actor : new HostInviteActor(invite.InvitedByUserId, invite.InvitedByOrgId, false)))))
        {
            // The quota is the host org's; an admin has none, so an admin's resend neither is blocked by it nor uses it.
            if (!actor.IsAdmin && invite.InvitedByOrgId is { } orgId)
            {
                await EnsureQuotaAsync(orgId, now, cancellationToken);
                if (!previouslyPending)
                    await EnsurePendingRoomAsync(orgId, now, cancellationToken);
            }

            if (await HasPendingFromAsync(invite.InvitedByUserId, invite.Email, invite.Id, now, cancellationToken))
                throw new DomainConflictException(HostInviteErrors.Duplicate, HostInviteErrors.DuplicateMessageKey);

            sendEmail = await EmailAllowsSendingAsync(actor, invite.Email, cancellationToken);

            invite.TokenHash = tokenHash;
            invite.ExpiresAt = expiresAt;
            db.HostInviteAuditEntries.Add(new HostInviteAuditEntry
            {
                InviteId = invite.Id,
                Action = HostInviteAuditAction.Resent,
                ActorUserId = actor.UserId,
                ActorOrgId = actor.IsAdmin ? null : actor.OrgId,
                OccurredAt = now,
            });
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        if (sendEmail && !emailQueue.Enqueue(invite.Email, content, EmailTemplates.Names.HostInvite))
            logger.LogWarning("Host invite {InviteId} resent but its email was not queued", invite.Id);

        logger.LogInformation(
            "Host invite {InviteId} for {MaskedEmail} resent by {ActorUserId}, expires {ExpiresAt}, emailed {Emailed}",
            invite.Id, LogRedaction.MaskEmail(invite.Email), actor.UserId, invite.ExpiresAt, sendEmail);
        return new HostInviteSent(invite.Id, invite.ExpiresAt);
    }

    public async Task RevokeAsync(HostInviteActor actorInfo, Guid inviteId, CancellationToken cancellationToken = default)
    {
        var actor = RequireActor(actorInfo);
        var now = Now();

        var invite = await FindForActorAsync(actor, inviteId, cancellationToken);
        if (HostInviteRules.StateOf(invite, now) != HostInviteState.Pending)
            throw new DomainConflictException(HostInviteErrors.NotPending, HostInviteErrors.NotPendingMessageKey);

        invite.RevokedAt = now;
        db.HostInviteAuditEntries.Add(new HostInviteAuditEntry
        {
            InviteId = invite.Id,
            Action = HostInviteAuditAction.Revoked,
            ActorUserId = actor.UserId,
            ActorOrgId = actor.IsAdmin ? null : actor.OrgId,
            OccurredAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Host invite {InviteId} for {MaskedEmail} revoked by {ActorUserId}",
            invite.Id, LogRedaction.MaskEmail(invite.Email), actor.UserId);
    }

    public async Task<HostInviteQuota> GetQuotaAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var now = Now();
        var limit = Options.DailyQuotaPerHost;
        var since = now - HostInviteRules.QuotaWindow;

        var used = await QuotaEntries(orgId, since)
            .Select(e => e.OccurredAt)
            .OrderBy(t => t)
            .ToListAsync(cancellationToken);

        var resetsAt = used.Count == 0 ? now.Add(HostInviteRules.QuotaWindow) : used[0].Add(HostInviteRules.QuotaWindow);
        return new HostInviteQuota(limit, used.Count, Math.Max(0, limit - used.Count), resetsAt);
    }

    public async Task<HostInviteLookup> LookupAsync(string? token, CancellationToken cancellationToken = default)
    {
        const string invalid = "Invalid";
        if (!SupplierInviteTokens.TryNormalize(token, out var normalized))
            return new HostInviteLookup(false, invalid, null, null);

        var tokenHash = SupplierInviteTokens.Hash(normalized);
        var invite = await db.HostInviteRecords.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);
        if (invite is null)
            return new HostInviteLookup(false, invalid, null, null);

        var state = HostInviteRules.StateOf(invite, Now());
        if (state != HostInviteState.Pending)
            return new HostInviteLookup(false, state.ToString(), null, null);

        string? inviterName = null;
        if (invite.InvitedByOrgId is not null)
        {
            var inviter = await db.Users.AsNoTracking()
                .Where(u => u.Id == invite.InvitedByUserId)
                .Select(u => new { u.FirstName, u.LastName })
                .FirstOrDefaultAsync(cancellationToken);
            inviterName = await InviterDisplayNameAsync(
                new HostInviteActor(invite.InvitedByUserId, invite.InvitedByOrgId, false), inviter?.FirstName, inviter?.LastName, cancellationToken);
        }

        return new HostInviteLookup(true, state.ToString(), inviterName, PersonalDataMasking.MaskEmail(invite.Email));
    }

    public async Task AcceptAsync(AcceptHostInvite request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);

        if (!SupplierInviteTokens.TryNormalize(request.Token, out var token))
            throw InvalidInvite();

        var tokenHash = SupplierInviteTokens.Hash(token);
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.HostInviteAccept, tokenHash));

        var invite = await db.HostInviteRecords.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken)
                     ?? throw InvalidInvite();
        var now = Now();

        if (invite.IsUsed)
        {
            // Sending the same acceptance twice finds it done; another account finds it taken.
            if (string.Equals(invite.UsedByUserId, request.UserId, StringComparison.Ordinal))
                return;
            throw new DomainConflictException(HostInviteErrors.Used, HostInviteErrors.UsedMessageKey);
        }

        if (invite.RevokedAt is not null)
            throw new DomainRuleException(HostInviteErrors.Revoked, HostInviteErrors.RevokedMessageKey);
        if (invite.ExpiresAt <= now)
            throw new DomainRuleException(HostInviteErrors.Expired, HostInviteErrors.ExpiredMessageKey);

        if (!request.EmailVerified)
            throw new DomainRuleException(HostInviteErrors.EmailNotVerified, HostInviteErrors.EmailNotVerifiedMessageKey);

        if (!string.Equals(HostInviteRules.NormalizeEmail(request.Email), invite.Email, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Host invite {InviteId} not accepted: the email of user {UserId} is not the invited one", invite.Id, request.UserId);
            throw new DomainRuleException(HostInviteErrors.EmailMismatch, HostInviteErrors.EmailMismatchMessageKey);
        }

        invite.IsUsed = true;
        invite.UsedAt = now;
        invite.UsedByUserId = request.UserId;
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Host invite {InviteId} accepted by user {UserId} (invited by {InvitedByUserId}, org {InvitedByOrgId})",
            invite.Id, request.UserId, invite.InvitedByUserId, invite.InvitedByOrgId);
    }

    // ─── helpers ───

    private HostInviteOptions Options => options.Value;

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static DomainRuleException InvalidInvite() => new(HostInviteErrors.Invalid, HostInviteErrors.InvalidMessageKey);

    private static HostInviteActor RequireActor(HostInviteActor? actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.UserId);
        if (!actor.IsAdmin && actor.OrgId is null)
            throw new ArgumentException("A host actor needs its org.", nameof(actor));
        return actor;
    }

    private static string LockKey(HostInviteActor actor) =>
        actor.IsAdmin || actor.OrgId is null ? $"user:{actor.UserId}" : $"org:{actor.OrgId.Value:N}";

    private static HostInviteAuditEntry Audit(Guid inviteId, HostInviteAuditAction action, HostInviteActor actor, DateTime now) => new()
    {
        InviteId = inviteId,
        Action = action,
        ActorUserId = actor.UserId,
        ActorOrgId = actor.IsAdmin ? null : actor.OrgId,
        OccurredAt = now,
    };

    /// <summary>The invite of an id, for the actor: a host reaches only its own (404 for any other, as for an unknown id).</summary>
    private async Task<HostInviteRecord> FindForActorAsync(HostInviteActor actor, Guid inviteId, CancellationToken cancellationToken)
    {
        var invite = await db.HostInviteRecords.FirstOrDefaultAsync(i => i.Id == inviteId, cancellationToken);
        if (invite is null || (!actor.IsAdmin && invite.InvitedByUserId != actor.UserId))
        {
            throw new NotFoundException($"Host invite {inviteId} not found")
            {
                Code = HostInviteErrors.NotFound,
                MessageKey = HostInviteErrors.NotFoundMessageKey,
            };
        }

        return invite;
    }

    private IQueryable<HostInviteAuditEntry> QuotaEntries(Guid orgId, DateTime since) =>
        db.HostInviteAuditEntries.AsNoTracking().Where(e =>
            e.ActorOrgId == orgId
            && e.OccurredAt > since
            && (e.Action == HostInviteAuditAction.Created || e.Action == HostInviteAuditAction.Resent));

    private async Task EnsureQuotaAsync(Guid orgId, DateTime now, CancellationToken cancellationToken)
    {
        var used = await QuotaEntries(orgId, now - HostInviteRules.QuotaWindow).CountAsync(cancellationToken);
        if (used >= Options.DailyQuotaPerHost)
        {
            logger.LogWarning("Host invite quota reached for org {OrgId}: {Used}/{Limit}", orgId, used, Options.DailyQuotaPerHost);
            throw new DomainTooManyRequestsException(HostInviteErrors.QuotaExceeded, HostInviteErrors.QuotaExceededMessageKey);
        }
    }

    private async Task EnsurePendingRoomAsync(Guid orgId, DateTime now, CancellationToken cancellationToken)
    {
        var pending = await db.HostInviteRecords.AsNoTracking()
            .Where(HostInviteRules.StatePredicate(HostInviteState.Pending, now))
            .CountAsync(i => i.InvitedByOrgId == orgId, cancellationToken);
        if (pending >= Options.MaxPendingPerHost)
        {
            logger.LogWarning("Host invite pending cap reached for org {OrgId}: {Pending}/{Max}", orgId, pending, Options.MaxPendingPerHost);
            throw new DomainTooManyRequestsException(HostInviteErrors.PendingLimit, HostInviteErrors.PendingLimitMessageKey);
        }
    }

    /// <summary>True when the inviter already has a pending invite for the email (another than <paramref name="exceptId"/>).</summary>
    private Task<bool> HasPendingFromAsync(string inviterUserId, string email, Guid? exceptId, DateTime now, CancellationToken cancellationToken) =>
        db.HostInviteRecords.AsNoTracking()
            .Where(HostInviteRules.StatePredicate(HostInviteState.Pending, now))
            .AnyAsync(i => i.InvitedByUserId == inviterUserId && i.Email == email && (exceptId == null || i.Id != exceptId), cancellationToken);

    /// <summary>
    /// Whether the email of the invite may be mailed: true when no account has it. When one does, a platform admin gets the
    /// explicit 409 <c>host_email_taken</c>; a host gets <c>false</c> and its request goes on as if the email went out (see the class
    /// remarks).
    /// </summary>
    private async Task<bool> EmailAllowsSendingAsync(HostInviteActor actor, string email, CancellationToken cancellationToken)
    {
        var taken = await db.Users.AsNoTracking().AnyAsync(u => u.Email.Trim().ToLower() == email, cancellationToken);
        if (!taken)
            return true;

        if (actor.IsAdmin)
            throw new DomainConflictException(HostInviteErrors.EmailTaken, HostInviteErrors.EmailTakenMessageKey);

        return false;
    }

    /// <summary>Name shown to the invitee for a host inviter: its own name, else the name of its org.</summary>
    private async Task<string?> InviterDisplayNameAsync(
        HostInviteActor actor, string? firstName, string? lastName, CancellationToken cancellationToken)
    {
        var name = $"{firstName} {lastName}".Trim();
        if (name.Length > 0)
            return name;

        if (actor.OrgId is not { } orgId)
            return null;

        var orgName = await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Name).FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(orgName) ? null : orgName.Trim();
    }

    private async Task<Dictionary<Guid, HostInviteInviter>> InvitersAsync(IReadOnlyList<HostInviteRecord> rows, CancellationToken cancellationToken)
    {
        var userIds = rows.Select(r => r.InvitedByUserId).Distinct().ToList();
        var orgIds = rows.Where(r => r.InvitedByOrgId != null).Select(r => r.InvitedByOrgId!.Value).Distinct().ToList();

        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var orgs = await db.Orgs.AsNoTracking()
            .Where(o => orgIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Name })
            .ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);

        var result = new Dictionary<Guid, HostInviteInviter>();
        foreach (var row in rows)
        {
            if (!users.TryGetValue(row.InvitedByUserId, out var user))
                continue;

            var name = $"{user.FirstName} {user.LastName}".Trim();
            if (name.Length == 0)
                name = user.Email;
            var orgName = row.InvitedByOrgId is { } orgId && orgs.TryGetValue(orgId, out var n) ? n : null;
            result[row.Id] = new HostInviteInviter(name, orgName);
        }

        return result;
    }

    private static HostInviteItem ToItem(HostInviteRecord invite, DateTime now, HostInviteInviter? inviter) => new(
        invite.Id,
        invite.Email,
        HostInviteRules.StateOf(invite, now),
        invite.Message,
        invite.CreatedAt,
        invite.ExpiresAt,
        invite.UsedAt,
        inviter);

    private EmailContent BuildEmail(HostInviteRecord invite, string token, string? inviterName, DateTime? expiresAt = null) =>
        EmailTemplates.HostInvite(
            string.Equals(invite.Locale, "en", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("en")
                : EmailTemplates.DefaultCulture,
            invite.Email,
            inviterName,
            invite.Message,
            publicSiteLinks.HostInviteSignup(token),
            expiresAt ?? invite.ExpiresAt);
}
