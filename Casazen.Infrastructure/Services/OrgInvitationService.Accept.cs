using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The acceptance of an invitation (AM-02, <c>docs/runbooks/org-team.md</c> § 4). Order of the checks, and why:
/// the link first (an old or foreign link says nothing but "invalid"); then the account (never staff; a verified email that
/// is the invited one); then the state of the invitation, so an old link says why it no longer works; then the org of the
/// account; and only then, under the locks, the writes.
/// </summary>
public sealed partial class OrgInvitationService
{
    /// <summary>Context key of the staff console: whoever has a membership of it is a platform admin.</summary>
    private const string PlatformAdminContext = "admin";

    /// <summary>
    /// The Auth0 roles the onboarding gives an owner by rental type; a person who leaves its empty org for another loses them,
    /// whatever the rental type it chose (only the roles it can hold are removed, the others are left alone).
    /// </summary>
    private static readonly UserRole[] OnboardingOwnerRoles = [UserRole.PropertyOwner, UserRole.LongTermLandlord];

    public async Task<OrgInvitationAccepted> AcceptAsync(AcceptOrgInvitation request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!OrgInvitationTokens.TryNormalize(request.Token, out var token))
            throw Gone(OrgInvitationErrors.Invalid, "InvitationInvalid");

        // The consents are checked before anything is read or written, so a refusal never depends on a rollback.
        var (consentsValid, consentsError) = onboarding.ValidateConsents(request.Consents, requireConsents: true);
        if (!consentsValid)
            throw ConsentsRefused(consentsError);

        var hash = OrgInvitationTokens.Hash(token);
        var now = Now;

        // Read without tracking: it is read again, tracked, under the locks.
        var seen = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        if (seen is null || !OrgInvitationTokens.Matches(seen.TokenHash, token))
            throw Gone(OrgInvitationErrors.Invalid, "InvitationInvalid");

        var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken)
                      ?? throw new NotFoundException($"User {request.UserId} not found");

        // Staff never join a customer's org, whatever the invitation says.
        if (await IsPlatformAdminAsync(request.CallerIsPlatformAdmin, account, cancellationToken))
            throw new DomainForbiddenException(OrgInvitationErrors.PlatformAdmin, "InvitationPlatformAdmin");

        // The same person sending the acceptance twice (double click, retry after a lost response) finds it done.
        if (await IsReplayAsync(seen, request.UserId, cancellationToken))
            return await ReplayAsync(seen, cancellationToken);

        EnsureAcceptable(seen, now);

        if (!request.AccountEmailVerified)
            throw new DomainForbiddenException(OrgInvitationErrors.EmailNotVerified, "InvitationEmailNotVerified");
        if (!string.Equals(OrgInvitationRules.NormalizeEmail(request.AccountEmail), seen.Email, StringComparison.Ordinal))
            throw new DomainForbiddenException(OrgInvitationErrors.EmailMismatch, "InvitationEmailMismatch");

        var targetOrg = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == seen.OrgId, cancellationToken);
        if (targetOrg is not { IsActive: true, OrgType: OrgType.Host })
            throw Gone(OrgInvitationErrors.Invalid, "InvitationInvalid");

        // What the account is in an org today decides: join, leave an empty org of its own for this one, or refuse.
        AcceptPlan plan;
        try
        {
            plan = await PlanAsync(request.UserId, seen.OrgId, cancellationToken);
        }
        catch (DomainConflictException ex) when (ex.Code == OrgMembershipErrors.AlreadyMember)
        {
            // The same person's other request (double click, retry) accepted this very invitation between the read above
            // and this one: the member it created is what the plan sees. That is a replay, not a conflict.
            if (await TryReplayAsync(seen.Id, request.UserId, cancellationToken) is { } replay)
                return replay;

            throw;
        }

        await EnsureOldOrgIsEmptyAsync(plan, request.UserId, cancellationToken);

        OrgInvitation invitation;
        await using (var transaction = await BeginAcceptTransactionAsync(seen.OrgId, plan, cancellationToken))
        {
            // Again, tracked, now that nobody else can change the seats of the org or the org being left.
            // Resend, copy link and the reminder replace the token under this same lock, and the expiry is a moment the
            // wait itself can pass. The secret and the clock from before the lock are already stale.
            invitation = await db.OrgInvitations.IgnoreQueryFilters().FirstAsync(i => i.Id == seen.Id, cancellationToken);
            if (!OrgInvitationTokens.Matches(invitation.TokenHash, token))
                throw Gone(OrgInvitationErrors.Invalid, "InvitationInvalid");
            now = Now;

            if (await IsReplayAsync(invitation, request.UserId, cancellationToken))
                return await ReplayAsync(invitation, cancellationToken);

            EnsureAcceptable(invitation, now);

            var lockedPlan = await PlanAsync(request.UserId, invitation.OrgId, cancellationToken);
            if (lockedPlan != plan)
                throw new DomainConflictException(OrgInvitationErrors.UserHasOrganization, "InvitationUserHasOrganization");
            await EnsureOldOrgIsEmptyAsync(lockedPlan, request.UserId, cancellationToken);

            var user = await db.Users.FirstAsync(u => u.Id == request.UserId, cancellationToken);
            await LeaveTheOldOrgAsync(user, lockedPlan, now, cancellationToken);

            // «Solo alcuni» is the collaborator's (AM-03). The creation refuses it for another role, but an invitation sent
            // before that rule must still be accepted: the person reaches the whole org, as that role does.
            var propertyScope = invitation.Role == OrgRole.Collaborator ? invitation.PropertyScope : PropertyScope.All;

            // The member and every membership of its role, in this transaction.
            try
            {
                await orgMembership.AddMemberAsync(
                    user.Id,
                    invitation.OrgId,
                    invitation.Role,
                    invitation.Areas,
                    invitation.InvitedByUserId,
                    propertyScope,
                    cancellationToken);
            }
            catch (DomainConflictException ex) when (ex.Code is OrgMembershipErrors.AlreadyMember or OrgMembershipErrors.OtherOrg)
            {
                // Another organisation's invitation got the person first (the unique index of the member row decided):
                // for the person it is the same answer as having an organisation to begin with. Nothing is saved.
                throw new DomainConflictException(OrgInvitationErrors.UserHasOrganization, "InvitationUserHasOrganization");
            }

            invitation.Status = OrgInvitationStatus.Accepted;
            invitation.AcceptedAt = now;
            invitation.AcceptedByUserId = user.Id;
            invitation.ClosedAt = now;
            invitation.UpdatedAt = now;

            // The person is onboarded as a member: the host gate wants the onboarding completed and the consents of the
            // org it joins (decision D14: the four of the onboarding, again).
            user.OnboardingCompletedAt ??= now;
            user.UpdatedAt = now;

            var consents = await onboarding.ValidateAndRecordConsentsAsync(
                user.Id, invitation.OrgId, request.Consents, requireConsents: true, request.ClientIp, cancellationToken);
            if (!consents.Success)
                throw ConsentsRefused(consents.Error);

            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        // The inner writes invalidated the cache before the commit; once more now that it is committed.
        authorizationCache.Invalidate(request.UserId);

        if (plan.OldOrgId is not null)
            await RemoveOwnerRolesFromAuth0Async(request.UserId, cancellationToken);

        logger.LogInformation(
            "Org invitation accepted: invitationId={InvitationId} orgId={OrgId} userId={UserId} role={Role} leftEmptyOrg={LeftEmptyOrg}",
            invitation.Id, invitation.OrgId, request.UserId, invitation.Role, plan.OldOrgId is not null);

        return new OrgInvitationAccepted(
            invitation.OrgId,
            OrgName(targetOrg),
            invitation.Role,
            invitation.Areas.ToList(),
            LeftEmptyOrg: plan.OldOrgId is not null);
    }

    // ─── What the account is today ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the acceptance does with the org the account already has.
    /// </summary>
    /// <param name="OldOrgId">
    /// The host org the account leaves because it is its owner and empty (<see cref="IOrgEmptinessChecker"/> decides
    /// whether it really is); <c>null</c> when the account has no org to leave.
    /// </param>
    /// <param name="DetachedLegacyOrgId">
    /// A legacy link of <c>User.OrgId</c> to an org that is not a host org (a supplier's own org, or a dangling id): it is
    /// not a tenant, it only has to stop being in the way. The supplier link is kept on <c>User.SupplierOrgId</c>.
    /// </param>
    private sealed record AcceptPlan(Guid? OldOrgId, Guid? DetachedLegacyOrgId);

    /// <summary>
    /// The plan for <paramref name="userId"/> joining <paramref name="targetOrgId"/>, or the refusal:
    /// already a member of the org (409 <c>org_member_already_member</c>); a member who is not an owner, or the owner of an
    /// org whose member row does not match its link, of another org (409 <c>invitation_user_has_organization</c>, no
    /// merging). Whether the org of an owner can be left is decided next, by the emptiness check.
    /// </summary>
    private async Task<AcceptPlan> PlanAsync(string userId, Guid targetOrgId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken);
        var member = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);

        if (member is not null)
        {
            if (member.OrgId == targetOrgId)
                throw new DomainConflictException(OrgMembershipErrors.AlreadyMember, "OrgMemberAlreadyMember");

            if (member.Role != OrgRole.Owner || user.OrgId != member.OrgId)
                throw new DomainConflictException(OrgInvitationErrors.UserHasOrganization, "InvitationUserHasOrganization");

            return new AcceptPlan(member.OrgId, null);
        }

        if (user.OrgId is not Guid current || current == targetOrgId)
            return new AcceptPlan(null, null);

        var type = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == current)
            .Select(o => (OrgType?)o.OrgType)
            .FirstOrDefaultAsync(cancellationToken);

        // A host org without an owner row (an owner the backfill did not know): it can be left only if it is empty too.
        return type == OrgType.Host ? new AcceptPlan(current, null) : new AcceptPlan(null, current);
    }

    /// <summary>409 <c>invitation_user_has_organization</c> unless the org being left is empty and unbilled.</summary>
    private async Task EnsureOldOrgIsEmptyAsync(AcceptPlan plan, string userId, CancellationToken cancellationToken)
    {
        if (plan.OldOrgId is not Guid oldOrgId)
            return;

        var result = await emptiness.CheckAsync(oldOrgId, userId, cancellationToken);
        if (result.IsEmpty)
            return;

        logger.LogInformation(
            "Org invitation refused, the org of the account is in use: userId={UserId} orgId={OrgId} blockers=[{Blockers}]",
            userId, oldOrgId, string.Join(", ", result.Blockers));
        throw new DomainConflictException(OrgInvitationErrors.UserHasOrganization, "InvitationUserHasOrganization");
    }

    /// <summary>
    /// Takes the locks of the acceptance in a fixed order: the seats lock of the org joined and, when an empty org is
    /// left, its seats lock too (ordered by org id, so two acceptances never wait for each other in a circle) and its
    /// property slot (a property being created for it at this very moment finishes first, or finds the org gone).
    /// </summary>
    private Task<IDbContextTransaction?> BeginAcceptTransactionAsync(
        Guid targetOrgId,
        AcceptPlan plan,
        CancellationToken cancellationToken)
    {
        var locks = new List<(PostgresAdvisoryLocks.Scope Scope, string Key)>();
        if (plan.OldOrgId is Guid oldOrgId)
        {
            locks.AddRange(new[] { targetOrgId, oldOrgId }.Order().Select(OrgSeatService.SeatsLock));
            locks.Add((PostgresAdvisoryLocks.Scope.OrgPropertySlot, oldOrgId.ToString("N")));
        }
        else
        {
            locks.Add(OrgSeatService.SeatsLock(targetOrgId));
        }

        return PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, [.. locks]);
    }

    /// <summary>
    /// Stages and saves the end of everything the account had in the org it leaves (the owner row and the memberships,
    /// through <see cref="IOrgMembershipService.AbandonEmptyOrgAsync"/>), deactivates that org (its consents and the rest
    /// stay as they are) and takes the owner's marks off the account. Or, for a legacy link to an org that is not a host
    /// org, only unlinks it.
    /// </summary>
    private async Task LeaveTheOldOrgAsync(User user, AcceptPlan plan, DateTime now, CancellationToken cancellationToken)
    {
        if (plan.DetachedLegacyOrgId is Guid legacy)
        {
            if (user.SupplierOrgId is null
                && await db.Orgs.AsNoTracking().AnyAsync(o => o.Id == legacy && o.OrgType == OrgType.Supplier, cancellationToken))
                user.SupplierOrgId = legacy;

            user.OrgId = null;
            user.UpdatedAt = now;
        }

        if (plan.OldOrgId is not Guid oldOrgId)
            return;

        var oldOrg = await db.Orgs.FirstAsync(o => o.Id == oldOrgId, cancellationToken);
        await orgMembership.AbandonEmptyOrgAsync(user.Id, oldOrgId, cancellationToken);

        oldOrg.IsActive = false;
        oldOrg.UpdatedAt = now;

        // It was an owner: no longer the rental type it chose, and no owner role in the DB.
        user.Role = UserRole.None;
        user.RentalType = null;
        user.UpdatedAt = now;
    }

    /// <summary>
    /// Takes the owner roles of the onboarding off the account in Auth0, best effort after the commit: the DB no longer
    /// gives it anything of the org it left, and the token roles only complete the contexts of someone who is not a
    /// member (a member's contexts come from the DB) and no longer open the billing of an org to a non-owner member
    /// (<c>OrgBillingAdminAuthorizationHandler</c>). A failure is logged for an operator, the acceptance is not undone.
    /// </summary>
    private async Task RemoveOwnerRolesFromAuth0Async(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var sync = await auth0.RemoveRolesAsync(userId, OnboardingOwnerRoles, cancellationToken);
            if (!sync.Succeeded)
            {
                logger.LogWarning(
                    "Auth0 owner roles of user {UserId} not removed after it left its empty org ({ErrorCode}): an operator removes PropertyOwner and LongTermLandlord in Auth0 (runbook org-team.md)",
                    userId, sync.ErrorCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Auth0 owner roles of user {UserId} not removed after it left its empty org: an operator removes PropertyOwner and LongTermLandlord in Auth0 (runbook org-team.md)",
                userId);
        }
    }

    // ─── State of the invitation ────────────────────────────────────────────────────────────────────────

    /// <summary>410 with the reason the link cannot be used any more; nothing when it still can.</summary>
    private static void EnsureAcceptable(OrgInvitation invitation, DateTime now)
    {
        switch (invitation.Status)
        {
            case OrgInvitationStatus.Accepted:
                throw Gone(OrgInvitationErrors.Used, "InvitationUsed");
            case OrgInvitationStatus.Revoked:
                throw Gone(OrgInvitationErrors.Revoked, "InvitationRevoked");
            case OrgInvitationStatus.Expired:
                throw Gone(OrgInvitationErrors.Expired, "InvitationExpired");
        }

        if (!OrgInvitationRules.IsOpen(invitation.Status, invitation.ExpiresAt, now))
            throw Gone(OrgInvitationErrors.Expired, "InvitationExpired");
    }

    /// <summary>True when the invitation was accepted by this very account and the account still is an active member of the org.</summary>
    private async Task<bool> IsReplayAsync(OrgInvitation invitation, string userId, CancellationToken cancellationToken) =>
        invitation.Status == OrgInvitationStatus.Accepted
        && string.Equals(invitation.AcceptedByUserId, userId, StringComparison.Ordinal)
        && await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(
                m => m.UserId == userId && m.OrgId == invitation.OrgId && m.Status == OrgMemberStatus.Active,
                cancellationToken);

    /// <summary>The answer of the acceptance already done, when the invitation now is the one this account accepted; otherwise <c>null</c>.</summary>
    private async Task<OrgInvitationAccepted?> TryReplayAsync(Guid invitationId, string userId, CancellationToken cancellationToken)
    {
        var current = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invitationId, cancellationToken);

        return current is not null && await IsReplayAsync(current, userId, cancellationToken)
            ? await ReplayAsync(current, cancellationToken)
            : null;
    }

    private async Task<OrgInvitationAccepted> ReplayAsync(OrgInvitation invitation, CancellationToken cancellationToken)
    {
        var org = await db.Orgs.AsNoTracking().FirstAsync(o => o.Id == invitation.OrgId, cancellationToken);
        return new OrgInvitationAccepted(invitation.OrgId, OrgName(org), invitation.Role, invitation.Areas.ToList(), LeftEmptyOrg: false);
    }

    private async Task<bool> IsPlatformAdminAsync(bool tokenSaysAdmin, User account, CancellationToken cancellationToken) =>
        tokenSaysAdmin
        || account.Role == UserRole.Admin
        || await db.UserContextMemberships.AsNoTracking()
            .AnyAsync(m => m.UserId == account.Id && m.ContextKey == PlatformAdminContext, cancellationToken);

    private static string OrgName(Org org) => string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName;

    private static DomainGoneException Gone(string code, string messageKey) => new(code, messageKey);

    /// <summary>
    /// The consents sent were not valid (the controller checks them first, so this is for a direct caller): the same codes as
    /// the onboarding, as a 422.
    /// </summary>
    private static DomainRuleException ConsentsRefused(ConsentValidationError? error) =>
        error?.Type == ConsentValidationErrorType.StaleVersion
            ? new DomainRuleException("stale_documents", "ConsentsStale")
            : new DomainRuleException("consents_incomplete", "ConsentsIncomplete");
}
