using System.Globalization;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgInvitationService" />
/// <remarks>
/// <para><b>One unit of work per call.</b> Every write is one transaction on PostgreSQL holding the org's seats lock
/// (<see cref="PostgresAdvisoryLocks.Scope.OrgSeats"/>): the seat check and the write that takes the seat can never be
/// interleaved by another request, which is what gives the last seat to one person only. The queries inside read with
/// <c>IgnoreQueryFilters</c>, scoped to the org or the token explicitly: the request's tenant is not necessarily the org of
/// the invitation (the acceptance of a person who has no org yet, or has another).</para>
/// <para><b>The email goes out after the commit</b>, never inside the transaction, and a failure to queue it does not undo
/// the invitation: the result says <c>EmailQueued = false</c> and the link can be copied or the invitation sent again.</para>
/// <para>The token is never logged and never stored; emails are logged masked. The acceptance is in
/// <c>OrgInvitationService.Accept.cs</c>.</para>
/// </remarks>
public sealed partial class OrgInvitationService(
    AppDbContext db,
    IOrgSeatService seats,
    IOrgMembershipService orgMembership,
    IOrgEmptinessChecker emptiness,
    IOnboardingService onboarding,
    IAuth0ManagementService auth0,
    IUserAuthorizationCache authorizationCache,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IActivityLog activityLog,
    IOrgPropertyAccessService propertyAccess,
    IConfiguration configuration,
    ILogger<OrgInvitationService> logger,
    TimeProvider? timeProvider = null) : IOrgInvitationService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Invitation emails an org may send in 24 hours (<c>OrgTeam:InvitationDailyLimit</c>, AM-D15); a value that is not a positive number is ignored.</summary>
    private int DailyLimit => ReadPositive(OrgInvitationRules.DailyLimitConfigKey, OrgInvitationRules.DefaultDailyLimit);

    /// <summary>Minutes between two «reinvia» of one invitation (<c>OrgTeam:ResendMinutes</c>, AM-D15); a value that is not a positive number is ignored.</summary>
    private TimeSpan ResendWait => TimeSpan.FromMinutes(ReadPositive(OrgInvitationRules.ResendMinutesConfigKey, OrgInvitationRules.DefaultResendMinutes));

    private int ReadPositive(string key, int fallback) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    public async Task<OrgInvitationSent> CreateAsync(CreateOrgInvitation request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Role == OrgRole.Owner)
            throw new DomainRuleException(OrgMembershipErrors.OwnerNotAssignable, "OrgMemberOwnerNotAssignable");

        // «Solo alcuni» is the collaborator's (AM-03): refused here, not at the acceptance, where the inviter is gone.
        if (request.PropertyScope == PropertyScope.Selected && request.Role != OrgRole.Collaborator)
            throw new DomainRuleException(OrgMembershipErrors.ScopeNotSupported, "OrgMemberScopeNotSupported");

        // The properties of the invitation (AM-02c) are the «Solo alcuni» collaborator's too, and only that one's.
        var propertyIds = (request.PropertyIds ?? []).Distinct().Order().ToList();
        if (propertyIds.Count > 0 && (request.PropertyScope != PropertyScope.Selected || request.Role != OrgRole.Collaborator))
            throw new DomainRuleException(OrgMembershipErrors.ScopeNotSupported, "OrgMemberScopeNotSupported");

        var areas = NormalizeAreas(request.Areas);
        if (areas.Count == 0)
            throw new DomainRuleException(OrgMembershipErrors.AreaRequired, "OrgMemberAreaRequired");

        var email = OrgInvitationRules.NormalizeEmail(request.Email);
        var name = (request.Name ?? string.Empty).Trim();
        if (email.Length is 0 or > OrgInvitationRules.MaxEmailLength || name.Length is 0 or > OrgInvitationRules.MaxNameLength)
            throw new ArgumentException("The invitation needs an email and a name within their limits.", nameof(request));

        var actor = await OrgTeamAccess.RequireManagerAsync(db, request.OrgId, request.ActorUserId, cancellationToken);
        if (!OrgTeamRules.CanAssign(actor.Role, request.Role))
            throw new DomainForbiddenException(OrgInvitationErrors.OwnerRequired, "OrgOwnerRequired");

        // Every property is a live property of the org, and the inviter reaches it: the owner and the administrators, who are
        // the only ones who invite (RequireManagerAsync), reach every property of their org (HostScopeResolver).
        if (propertyIds.Count > 0)
            await EnsurePropertiesOfTheOrgAsync(request.OrgId, propertyIds, cancellationToken);

        // A missing public URL is a configuration error, found before anything is saved.
        publicSiteLinks.EnsureConfigured();

        var now = Now;
        var token = OrgInvitationTokens.Generate();
        var invitation = new OrgInvitation
        {
            OrgId = request.OrgId,
            Email = email,
            Name = name,
            Role = request.Role,
            Areas = areas,
            PropertyScope = request.PropertyScope,
            TokenHash = OrgInvitationTokens.Hash(token),
            Status = OrgInvitationStatus.Pending,
            ExpiresAt = now + OrgInvitationRules.Validity,
            InvitedByUserId = actor.UserId,
            Language = OrgInvitationRules.NormalizeLanguage(request.Language),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using (var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, request.OrgId, cancellationToken))
        {
            // A pending row of this email that is past its expiry would still hold the unique index.
            await CloseOverdueOfEmailAsync(request.OrgId, email, now, cancellationToken);

            if (await IsMemberByEmailAsync(request.OrgId, email, cancellationToken))
                throw new DomainConflictException(OrgMembershipErrors.AlreadyMember, "OrgMemberAlreadyMember");
            if (await HasOpenInvitationAsync(request.OrgId, email, exceptId: null, now, cancellationToken))
                throw new DomainConflictException(OrgInvitationErrors.AlreadyPending, "OrgInvitationAlreadyPending");

            // The emails of the org are capped (AM-D15). Under the seats lock, like the check of the seat: two requests at the
            // tenth place cannot both pass.
            await EnsureUnderDailyLimitAsync(request.OrgId, now, cancellationToken);
            await seats.EnsureSeatAvailableAsync(request.OrgId, cancellationToken);

            // The line of the activity log goes in the same save as the invitation: no invitation without its line, no line without it.
            // The properties it carries go in the same save too (AM-02c).
            RecordSend(invitation, now);
            db.OrgInvitations.Add(invitation);
            db.OrgInvitationProperties.AddRange(propertyIds.Select(id => new OrgInvitationProperty
            {
                OrgId = request.OrgId,
                InvitationId = invitation.Id,
                PropertyId = id,
            }));
            activityLog.Record(OrgActivity.Of(
                request.OrgId,
                OrgActivityType.MemberInvited,
                actor.UserId,
                invitation.Id.ToString(),
                (OrgActivityDetailKeys.Role, request.Role.ToString())));
            await SaveInvitationAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Org invitation created: invitationId={InvitationId} orgId={OrgId} role={Role} areas=[{Areas}] scope={Scope} by={InvitedBy} to={MaskedEmail}",
            invitation.Id, invitation.OrgId, invitation.Role, string.Join(", ", areas), invitation.PropertyScope, actor.UserId,
            LogRedaction.MaskEmail(email));

        var queued = await QueueInvitationAsync(invitation, token, cancellationToken);
        if (!queued)
            await UndoSendAsync(invitation, previousLastSentAt: null, previousSendLog: [], cancellationToken);

        return new OrgInvitationSent(ToView(invitation, now, propertyIds), queued);
    }

    public async Task<IReadOnlyList<OrgInvitationView>> ListAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var rows = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrgId == orgId && (i.Status == OrgInvitationStatus.Pending || i.Status == OrgInvitationStatus.Expired))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        // The properties they carry (AM-02c), in one more query for all of them.
        var propertiesOf = new Dictionary<Guid, IReadOnlyList<Guid>>();
        var invitationIds = rows.Select(i => i.Id).ToList();
        if (invitationIds.Count > 0)
        {
            var carried = await db.OrgInvitationProperties.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrgId == orgId && invitationIds.Contains(p.InvitationId))
                .Select(p => new { p.InvitationId, p.PropertyId })
                .ToListAsync(cancellationToken);
            foreach (var group in carried.GroupBy(p => p.InvitationId))
                propertiesOf[group.Key] = group.Select(p => p.PropertyId).Order().ToList();
        }

        return rows.Select(i => ToView(i, now, propertiesOf.GetValueOrDefault(i.Id) ?? [])).ToList();
    }

    public async Task<OrgInvitationSent> ResendAsync(
        Guid orgId,
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        var actor = await OrgTeamAccess.RequireManagerAsync(db, orgId, actorUserId, cancellationToken);
        publicSiteLinks.EnsureConfigured();

        var now = Now;
        var token = OrgInvitationTokens.Generate();
        OrgInvitation invitation;
        DateTime? previousLastSentAt;
        List<DateTime> previousSendLog;

        await using (var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken))
        {
            invitation = await FindAsync(orgId, invitationId, cancellationToken);
            // Sending again gives the role once more (an expired one is revived): the same rule as creating it.
            EnsureMayHandle(actor, invitation, assigning: true);
            if (invitation.Status is OrgInvitationStatus.Accepted or OrgInvitationStatus.Revoked)
                throw new DomainConflictException(OrgInvitationErrors.NotPending, "OrgInvitationNotPending");

            // AM-D15: one «reinvia» an hour for each invitation, and the cap of the org on all of its emails.
            EnsureResendIsNotTooSoon(invitation, now);
            await EnsureUnderDailyLimitAsync(orgId, now, cancellationToken);

            // An expired invitation holds no seat: bringing it back takes one again, and must not collide with a newer
            // invitation of the same person or with the person having joined meanwhile.
            if (!OrgInvitationRules.IsOpen(invitation.Status, invitation.ExpiresAt, now))
            {
                if (await IsMemberByEmailAsync(orgId, invitation.Email, cancellationToken))
                    throw new DomainConflictException(OrgMembershipErrors.AlreadyMember, "OrgMemberAlreadyMember");
                if (await HasOpenInvitationAsync(orgId, invitation.Email, invitation.Id, now, cancellationToken))
                    throw new DomainConflictException(OrgInvitationErrors.AlreadyPending, "OrgInvitationAlreadyPending");

                await seats.EnsureSeatAvailableAsync(orgId, cancellationToken);
            }

            previousLastSentAt = invitation.LastSentAt;
            previousSendLog = [.. invitation.SendLog];

            invitation.TokenHash = OrgInvitationTokens.Hash(token);
            invitation.Status = OrgInvitationStatus.Pending;
            invitation.ExpiresAt = now + OrgInvitationRules.Validity;
            invitation.ReminderSentAt = null;
            invitation.ClosedAt = null;
            invitation.UpdatedAt = now;
            RecordSend(invitation, now);
            await SaveInvitationAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Org invitation sent again: invitationId={InvitationId} orgId={OrgId} by={ActorUserId} to={MaskedEmail}",
            invitation.Id, orgId, actorUserId, LogRedaction.MaskEmail(invitation.Email));

        var queued = await QueueInvitationAsync(invitation, token, cancellationToken);
        if (!queued)
            await UndoSendAsync(invitation, previousLastSentAt, previousSendLog, cancellationToken);

        return new OrgInvitationSent(ToView(invitation, now, await PropertyIdsOfAsync(orgId, invitation.Id, cancellationToken)), queued);
    }

    public async Task RevokeAsync(
        Guid orgId,
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        var actor = await OrgTeamAccess.RequireManagerAsync(db, orgId, actorUserId, cancellationToken);

        var now = Now;
        await using var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken);

        var invitation = await FindAsync(orgId, invitationId, cancellationToken);
        EnsureMayHandle(actor, invitation, assigning: false);
        switch (invitation.Status)
        {
            case OrgInvitationStatus.Revoked:
                // A second click on "Revoke" finds the work done.
                return;
            case OrgInvitationStatus.Accepted:
                throw new DomainConflictException(OrgInvitationErrors.NotPending, "OrgInvitationNotPending");
        }

        invitation.Status = OrgInvitationStatus.Revoked;
        invitation.ClosedAt ??= now;
        invitation.UpdatedAt = now;
        activityLog.Record(OrgActivity.Of(
            orgId,
            OrgActivityType.InvitationRevoked,
            actor.UserId,
            invitation.Id.ToString(),
            (OrgActivityDetailKeys.Role, invitation.Role.ToString())));
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Org invitation revoked: invitationId={InvitationId} orgId={OrgId} by={ActorUserId} to={MaskedEmail}",
            invitation.Id, orgId, actorUserId, LogRedaction.MaskEmail(invitation.Email));
    }

    public async Task<OrgInvitationLink> CopyLinkAsync(
        Guid orgId,
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        var actor = await OrgTeamAccess.RequireManagerAsync(db, orgId, actorUserId, cancellationToken);
        publicSiteLinks.EnsureConfigured();

        var now = Now;
        var token = OrgInvitationTokens.Generate();
        OrgInvitation invitation;

        await using (var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken))
        {
            invitation = await FindAsync(orgId, invitationId, cancellationToken);
            EnsureMayHandle(actor, invitation, assigning: false);
            if (!OrgInvitationRules.IsOpen(invitation.Status, invitation.ExpiresAt, now))
                throw new DomainConflictException(OrgInvitationErrors.NotPending, "OrgInvitationNotPending");

            // Only the hash is stored, so a link to hand out is a new token: the previous link, the one of the email
            // included, stops working. The expiry and the reminder are left as they are, and nothing is sent.
            invitation.TokenHash = OrgInvitationTokens.Hash(token);
            invitation.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Org invitation link copied: invitationId={InvitationId} orgId={OrgId} by={ActorUserId}",
            invitation.Id, orgId, actorUserId);

        return new OrgInvitationLink(invitation.Id, publicSiteLinks.OrgInvitationAccept(token), invitation.ExpiresAt);
    }

    public async Task<OrgInvitationPreview?> LookupAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!OrgInvitationTokens.TryNormalize(token, out var normalized))
            return null;

        var hash = OrgInvitationTokens.Hash(normalized);
        var now = Now;

        // One query, the same for every outcome. IgnoreQueryFilters: anonymous, there is no tenant.
        var row = await (
                from i in db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
                join o in db.Orgs.AsNoTracking() on i.OrgId equals o.Id
                where i.TokenHash == hash
                select new
                {
                    i.TokenHash,
                    i.Status,
                    i.ExpiresAt,
                    i.Email,
                    i.Name,
                    i.Role,
                    i.Areas,
                    OrgName = o.DisplayName,
                    FallbackName = o.Name,
                    o.IsActive,
                    o.OrgType,
                })
            .FirstOrDefaultAsync(cancellationToken);

        // Malformed, unknown, replaced by a newer link, expired, used, revoked, of an org that no longer exists: the same
        // answer, so a stranger learns nothing.
        if (row is null
            || !OrgInvitationTokens.Matches(row.TokenHash, normalized)
            || !OrgInvitationRules.IsOpen(row.Status, row.ExpiresAt, now)
            || !row.IsActive
            || row.OrgType != OrgType.Host)
            return null;

        return new OrgInvitationPreview(
            string.IsNullOrWhiteSpace(row.OrgName) ? row.FallbackName : row.OrgName,
            row.Email,
            row.Name,
            row.Role,
            row.Areas.ToList(),
            row.ExpiresAt);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static List<string> NormalizeAreas(IEnumerable<string>? areas) =>
        (areas ?? [])
            .Select(a => (a ?? string.Empty).Trim())
            .Where(OrgRoleCatalog.IsRentalContext)
            .Select(a => a.ToLowerInvariant())
            .Distinct()
            .ToList();

    /// <summary>
    /// 403 <see cref="OrgInvitationErrors.OwnerRequired"/> unless <paramref name="actor"/> may give this invitation's role
    /// (<paramref name="assigning"/>: resend, including the revival of an expired one) or act on it (revoke, copy link).
    /// Only the owner creates or touches an administrator (decision D15); <see cref="CreateAsync"/> applies the same rule.
    /// </summary>
    private static void EnsureMayHandle(OrgMember actor, OrgInvitation invitation, bool assigning)
    {
        var allowed = assigning
            ? OrgTeamRules.CanAssign(actor.Role, invitation.Role)
            : OrgTeamRules.CanActOn(actor.Role, invitation.Role);
        if (!allowed)
            throw new DomainForbiddenException(OrgInvitationErrors.OwnerRequired, "OrgOwnerRequired");
    }

    /// <summary>The invitation of the org, tracked, or 404 <see cref="OrgInvitationErrors.NotFound"/>.</summary>
    private async Task<OrgInvitation> FindAsync(Guid orgId, Guid invitationId, CancellationToken cancellationToken) =>
        await db.OrgInvitations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == invitationId && i.OrgId == orgId, cancellationToken)
        ?? throw new NotFoundException($"Org invitation {invitationId} not found")
        {
            Code = OrgInvitationErrors.NotFound,
            MessageKey = "OrgInvitationNotFound",
        };

    /// <summary>Marks as expired the invitations of this email that are pending in the table but past their expiry.</summary>
    private async Task CloseOverdueOfEmailAsync(Guid orgId, string email, DateTime now, CancellationToken cancellationToken)
    {
        var overdue = await db.OrgInvitations.IgnoreQueryFilters()
            .Where(i => i.OrgId == orgId
                        && i.Email == email
                        && i.Status == OrgInvitationStatus.Pending
                        && i.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        foreach (var invitation in overdue)
        {
            invitation.Status = OrgInvitationStatus.Expired;
            invitation.ClosedAt ??= invitation.ExpiresAt;
            invitation.UpdatedAt = now;
        }
    }

    /// <summary>True when somebody with this email already is a member of the org (active or deactivated).</summary>
    private async Task<bool> IsMemberByEmailAsync(Guid orgId, string email, CancellationToken cancellationToken) =>
        await (
                from m in db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
                join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                where m.OrgId == orgId && u.Email.ToLower() == email
                select m.Id)
            .AnyAsync(cancellationToken);

    private async Task<bool> HasOpenInvitationAsync(
        Guid orgId,
        string email,
        Guid? exceptId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var open = db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrgId == orgId
                        && i.Email == email
                        && i.Status == OrgInvitationStatus.Pending
                        && i.ExpiresAt > now);
        if (exceptId is { } except)
            open = open.Where(i => i.Id != except);

        return await open.AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Saves the staged invitation. Two parallel invitations of the same person race on the partial unique index; the
    /// loser (23505) answers 409 <see cref="OrgInvitationErrors.AlreadyPending"/> like the explicit check does.
    /// </summary>
    private async Task SaveInvitationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, "UIX_OrgInvitations_OrgId_Email_Pending"))
        {
            db.ChangeTracker.Clear();
            throw new DomainConflictException(OrgInvitationErrors.AlreadyPending, "OrgInvitationAlreadyPending");
        }
    }

    private static OrgInvitationView ToView(OrgInvitation invitation, DateTime now, IReadOnlyList<Guid> propertyIds) => new(
        invitation.Id,
        invitation.Email,
        invitation.Name,
        invitation.Role,
        invitation.Areas.ToList(),
        invitation.PropertyScope,
        invitation.Status == OrgInvitationStatus.Pending && invitation.ExpiresAt <= now
            ? OrgInvitationStatus.Expired
            : invitation.Status,
        invitation.CreatedAt,
        invitation.ExpiresAt,
        invitation.ReminderSentAt,
        invitation.InvitedByUserId,
        propertyIds,
        invitation.LastSentAt);

    /// <summary>
    /// The properties an invitation carries (AM-02c), sorted; none for an ordinary one. Sorted here and not by the database, so the
    /// order is the one of the creation and of the list (PostgreSQL orders a uuid byte by byte, .NET does not).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> PropertyIdsOfAsync(Guid orgId, Guid invitationId, CancellationToken cancellationToken) =>
        (await db.OrgInvitationProperties.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrgId == orgId && p.InvitationId == invitationId)
            .Select(p => p.PropertyId)
            .ToListAsync(cancellationToken))
        .Order()
        .ToList();

    /// <summary>
    /// 422 <see cref="OrgMembershipErrors.PropertyUnknown"/> unless every id is a live property of the org. The tenant filter is
    /// lifted and the org explicit (the soft-delete filter stays on: a deleted property is not one), like the rest of this service.
    /// </summary>
    private async Task EnsurePropertiesOfTheOrgAsync(Guid orgId, IReadOnlyCollection<Guid> propertyIds, CancellationToken cancellationToken)
    {
        var ids = propertyIds.ToList();
        var known = await db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
            .CountAsync(p => p.OrgId == orgId && ids.Contains(p.Id), cancellationToken);
        if (known != ids.Count)
            throw new DomainRuleException(OrgMembershipErrors.PropertyUnknown, "OrgMemberPropertyUnknown");
    }

    // ─── The cap on the emails (AM-02c, decision AM-D15) ────────────────────────────────────────────────

    /// <summary>
    /// Marks an email of the invitation as sent by a person's action at <paramref name="now"/> (the creation, «reinvia»): the last
    /// send, and the list the cap of the org counts, kept to the last 24 hours. Staged; the caller saves.
    /// </summary>
    private static void RecordSend(OrgInvitation invitation, DateTime now)
    {
        var cutoff = now - OrgInvitationRules.DailyWindow;
        invitation.LastSentAt = now;
        invitation.SendLog = [.. invitation.SendLog.Where(sent => sent > cutoff), now];
    }

    /// <summary>
    /// 429 <see cref="OrgInvitationErrors.DailyLimitReached"/> when the org has already sent <c>OrgTeam:InvitationDailyLimit</c> emails
    /// of invitations (created or sent again, any state of the invitation: one revoked still counts) in the last 24 hours. Counts
    /// the emails in the list each invitation keeps, and reads only the invitations whose last send is inside the window.
    /// <b>The caller holds the seats lock of the org</b> and writes the send before releasing it, so two requests at the last
    /// place cannot both pass.
    /// </summary>
    private async Task EnsureUnderDailyLimitAsync(Guid orgId, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - OrgInvitationRules.DailyWindow;
        var logs = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrgId == orgId && i.LastSentAt >= cutoff)
            .Select(i => i.SendLog)
            .ToListAsync(cancellationToken);

        var sent = logs.Sum(log => log.Count(at => at >= cutoff));
        var limit = DailyLimit;
        if (sent >= limit)
        {
            logger.LogInformation(
                "Org invitation refused, the daily cap of emails is reached: orgId={OrgId} sent={Sent} limit={Limit}", orgId, sent, limit);
            throw new DomainTooManyRequestsException(OrgInvitationErrors.DailyLimitReached, "InvitationDailyLimitReached", limit);
        }
    }

    /// <summary>429 <see cref="OrgInvitationErrors.ResendTooSoon"/> while <c>OrgTeam:ResendMinutes</c> have not passed since the last email of the invitation; says how many minutes are left.</summary>
    private void EnsureResendIsNotTooSoon(OrgInvitation invitation, DateTime now)
    {
        if (invitation.LastSentAt is not { } last)
            return;

        var left = last + ResendWait - now;
        if (left <= TimeSpan.Zero)
            return;

        throw new DomainTooManyRequestsException(
            OrgInvitationErrors.ResendTooSoon,
            "InvitationResendTooSoon",
            (int)Math.Ceiling(left.TotalMinutes));
    }

    /// <summary>
    /// The email of an invitation could not be queued, so nothing went out: the person's allowance is given back (the last send
    /// and the list of the cap go back to what they were), so «reinvia» can be tried again at once and the cap does not count an
    /// email that was never sent. Best effort, after the commit: a failure is logged and leaves the allowance spent.
    /// </summary>
    private async Task UndoSendAsync(
        OrgInvitation invitation,
        DateTime? previousLastSentAt,
        List<DateTime> previousSendLog,
        CancellationToken cancellationToken)
    {
        try
        {
            invitation.LastSentAt = previousLastSentAt;
            invitation.SendLog = previousSendLog;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Org invitation {InvitationId}: the send that was not queued could not be taken back from the cap", invitation.Id);
        }
    }

    /// <summary>Renders and queues the invitation email (after the commit). False when it could not be queued.</summary>
    private async Task<bool> QueueInvitationAsync(OrgInvitation invitation, string token, CancellationToken cancellationToken)
    {
        var (orgName, inviterName) = await LoadSenderNamesAsync(invitation.OrgId, invitation.InvitedByUserId, cancellationToken);
        var content = OrgInvitationEmails.Invitation(publicSiteLinks, invitation, token, orgName, inviterName);

        var queued = emailQueue.Enqueue(invitation.Email, content, EmailTemplates.Names.OrgInvitation);
        if (!queued)
        {
            logger.LogWarning(
                "Org invitation {InvitationId} saved but its email was not queued (the link can be copied or the invitation sent again)",
                invitation.Id);
        }

        return queued;
    }

    /// <summary>The org's display name and the inviter's name (the org's name when the inviter has none or is gone).</summary>
    private async Task<(string OrgName, string InviterName)> LoadSenderNamesAsync(
        Guid orgId,
        string inviterUserId,
        CancellationToken cancellationToken)
    {
        var org = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.DisplayName, o.Name })
            .FirstOrDefaultAsync(cancellationToken);
        var orgName = org is null ? string.Empty : string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName;

        var inviter = await db.Users.AsNoTracking()
            .Where(u => u.Id == inviterUserId)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstOrDefaultAsync(cancellationToken);
        var inviterName = $"{inviter?.FirstName} {inviter?.LastName}".Trim();

        return (orgName, string.IsNullOrWhiteSpace(inviterName) ? orgName : inviterName);
    }
}
