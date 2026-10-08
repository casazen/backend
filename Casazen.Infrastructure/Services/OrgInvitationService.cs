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
    ILogger<OrgInvitationService> logger,
    TimeProvider? timeProvider = null) : IOrgInvitationService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<OrgInvitationSent> CreateAsync(CreateOrgInvitation request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Role == OrgRole.Owner)
            throw new DomainRuleException(OrgMembershipErrors.OwnerNotAssignable, "OrgMemberOwnerNotAssignable");

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

            await seats.EnsureSeatAvailableAsync(request.OrgId, cancellationToken);

            db.OrgInvitations.Add(invitation);
            await SaveInvitationAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Org invitation created: invitationId={InvitationId} orgId={OrgId} role={Role} areas=[{Areas}] scope={Scope} by={InvitedBy} to={MaskedEmail}",
            invitation.Id, invitation.OrgId, invitation.Role, string.Join(", ", areas), invitation.PropertyScope, actor.UserId,
            LogRedaction.MaskEmail(email));

        var queued = await QueueInvitationAsync(invitation, token, cancellationToken);
        return new OrgInvitationSent(ToView(invitation, now), queued);
    }

    public async Task<IReadOnlyList<OrgInvitationView>> ListAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var rows = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrgId == orgId && (i.Status == OrgInvitationStatus.Pending || i.Status == OrgInvitationStatus.Expired))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(i => ToView(i, now)).ToList();
    }

    public async Task<OrgInvitationSent> ResendAsync(
        Guid orgId,
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        await OrgTeamAccess.RequireManagerAsync(db, orgId, actorUserId, cancellationToken);
        publicSiteLinks.EnsureConfigured();

        var now = Now;
        var token = OrgInvitationTokens.Generate();
        OrgInvitation invitation;

        await using (var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken))
        {
            invitation = await FindAsync(orgId, invitationId, cancellationToken);
            if (invitation.Status is OrgInvitationStatus.Accepted or OrgInvitationStatus.Revoked)
                throw new DomainConflictException(OrgInvitationErrors.NotPending, "OrgInvitationNotPending");

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

            invitation.TokenHash = OrgInvitationTokens.Hash(token);
            invitation.Status = OrgInvitationStatus.Pending;
            invitation.ExpiresAt = now + OrgInvitationRules.Validity;
            invitation.ReminderSentAt = null;
            invitation.ClosedAt = null;
            invitation.UpdatedAt = now;
            await SaveInvitationAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Org invitation sent again: invitationId={InvitationId} orgId={OrgId} by={ActorUserId} to={MaskedEmail}",
            invitation.Id, orgId, actorUserId, LogRedaction.MaskEmail(invitation.Email));

        var queued = await QueueInvitationAsync(invitation, token, cancellationToken);
        return new OrgInvitationSent(ToView(invitation, now), queued);
    }

    public async Task RevokeAsync(
        Guid orgId,
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        await OrgTeamAccess.RequireManagerAsync(db, orgId, actorUserId, cancellationToken);

        var now = Now;
        await using var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken);

        var invitation = await FindAsync(orgId, invitationId, cancellationToken);
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
        await OrgTeamAccess.RequireManagerAsync(db, orgId, actorUserId, cancellationToken);
        publicSiteLinks.EnsureConfigured();

        var now = Now;
        var token = OrgInvitationTokens.Generate();
        OrgInvitation invitation;

        await using (var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(db, orgId, cancellationToken))
        {
            invitation = await FindAsync(orgId, invitationId, cancellationToken);
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

    private static OrgInvitationView ToView(OrgInvitation invitation, DateTime now) => new(
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
        invitation.InvitedByUserId);

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
