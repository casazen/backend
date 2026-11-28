using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>What the owner or an administrator writes to invite a person (<see cref="IOrgInvitationService.CreateAsync"/>).</summary>
/// <param name="OrgId">The inviter's org (read from the caller's own member row, never from the client).</param>
/// <param name="ActorUserId">The inviter (<c>User.Id</c>): must be the owner or an administrator of <paramref name="OrgId"/>.</param>
/// <param name="Email">The address the invitation is for; the person must accept with an account that has this very email.</param>
/// <param name="Name">The person's name, as the inviter knows it (shown in the team page and in the email).</param>
/// <param name="Role">The org role. Never <see cref="OrgRole.Owner"/>; <see cref="OrgRole.Admin"/> only from the owner.</param>
/// <param name="Areas">The areas the person works in: <c>short-rent</c> and/or <c>long-rent</c>.</param>
/// <param name="PropertyScope">All the properties, or only some: <c>Selected</c> is the collaborator's alone (422 <c>org_member_scope_not_supported</c> for another role).</param>
/// <param name="Language">Language of the emails: <c>it</c> (default) or <c>en</c>.</param>
/// <param name="PropertyIds">
/// The properties a <c>Selected</c> collaborator reaches once it accepts (AM-02c, AM-03-FU1), kept with the invitation and given in the
/// transaction of the acceptance. Only with <see cref="PropertyScope.Selected"/> and <see cref="OrgRole.Collaborator"/> (422
/// <c>org_member_scope_not_supported</c> otherwise); each must be a live property of the org the inviter reaches (422
/// <c>org_member_property_unknown</c>). <c>null</c> or empty: none, the owner gives them later.
/// </param>
public sealed record CreateOrgInvitation(
    Guid OrgId,
    string ActorUserId,
    string Email,
    string Name,
    OrgRole Role,
    IReadOnlyCollection<string> Areas,
    PropertyScope PropertyScope = PropertyScope.All,
    string? Language = null,
    IReadOnlyCollection<Guid>? PropertyIds = null);

/// <summary>
/// An invitation as the team page shows it: never the token or its hash. <paramref name="Status"/> is the <b>effective</b>
/// state: <see cref="OrgInvitationStatus.Pending"/> while it can still be accepted, <see cref="OrgInvitationStatus.Expired"/>
/// once its expiry has passed (also before the job marks it).
/// </summary>
/// <param name="PropertyIds">The properties a «Solo alcuni» collaborator gets on acceptance (AM-02c); empty otherwise. Ids only.</param>
/// <param name="LastSentAt">When an email of the invitation last went out by a person's action (the creation or the last «reinvia»); <c>null</c> for one written before it was tracked.</param>
public sealed record OrgInvitationView(
    Guid Id,
    string Email,
    string Name,
    OrgRole Role,
    IReadOnlyList<string> Areas,
    PropertyScope PropertyScope,
    OrgInvitationStatus Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? ReminderSentAt,
    string InvitedByUserId,
    IReadOnlyList<Guid> PropertyIds,
    DateTime? LastSentAt);

public sealed record OrgInvitationSent(OrgInvitationView Invitation, bool EmailQueued);

public sealed record OrgInvitationLink(Guid InvitationId, string Url, DateTime ExpiresAt);

public sealed record OrgInvitationPreview(
    string OrgName,
    string Email,
    string Name,
    OrgRole Role,
    IReadOnlyList<string> Areas,
    DateTime ExpiresAt);

/// <summary>
/// The acceptance of an invitation by a signed-in account (<see cref="IOrgInvitationService.AcceptAsync"/>).
/// </summary>
/// <param name="Token">The secret of the link.</param>
/// <param name="UserId">The signed-in account (<c>User.Id</c>, the Auth0 subject); its row must exist.</param>
/// <param name="AccountEmail">The email of the account, from the token or from Auth0 (never from the request body).</param>
/// <param name="AccountEmailVerified">Whether Auth0 verified that email.</param>
/// <param name="CallerIsPlatformAdmin">The token carries the <c>Admin</c> role: staff never join a customer's org.</param>
/// <param name="Consents">
/// Terms of Service, Privacy notice, DPA and subprocessors, accepted again for the org the person joins (decision D14).
/// </param>
/// <param name="ClientIp">Evidence of the consents.</param>
public sealed record AcceptOrgInvitation(
    string Token,
    string UserId,
    string AccountEmail,
    bool AccountEmailVerified,
    bool CallerIsPlatformAdmin,
    OnboardingConsentsInput Consents,
    string? ClientIp);

public sealed record OrgInvitationAccepted(
    Guid OrgId,
    string OrgName,
    OrgRole Role,
    IReadOnlyList<string> Areas,
    bool LeftEmptyOrg);

public static class OrgInvitationErrors
{
    public const string Invalid = "invitation_invalid";

    public const string Expired = "invitation_expired";
    public const string Used = "invitation_used";
    public const string Revoked = "invitation_revoked";

    public const string EmailMismatch = "invitation_email_mismatch";

    public const string EmailNotVerified = "invitation_email_not_verified";

    public const string PlatformAdmin = "invitation_platform_admin";

    public const string OwnerRequired = "org_owner_required";

    public const string UserHasOrganization = "invitation_user_has_organization";

    public const string AlreadyPending = "org_invitation_already_pending";

    public const string NotPending = "org_invitation_not_pending";

    public const string NotFound = "org_invitation_not_found";
    public const string MemberNotFound = "org_member_not_found";

    public const string DailyLimitReached = "invitation_daily_limit_reached";

    public const string ResendTooSoon = "invitation_resend_too_soon";
}

/// <summary>
/// Invitations to an org and the acceptance of them (AM-02, runbook <c>docs/runbooks/org-team.md</c>). The owner or an
/// administrator invites a person by email with a role; the person opens the link, signs in with the invited address and
/// accepts; the org member and its memberships are written by <see cref="IOrgMembershipService"/>.
/// </summary>
/// <remarks>
/// <para><b>Seats.</b> Creating, sending again and accepting run under the org's seats lock; a pending invitation holds a
/// seat, so accepting it changes nothing in the count, and revoking or expiring it frees the seat.</para>
/// <para><b>Token.</b> 256 random bits in the link, only its SHA-256 in the database
/// (<see cref="Casazen.Core.OrgTeam.OrgInvitationTokens"/>). Sending again, copying the link and the reminder rotate it.</para>
/// <para><b>Errors.</b> Domain refusals are exceptions with the codes of <see cref="OrgInvitationErrors"/>,
/// <see cref="OrgSeatErrors"/> and <see cref="OrgMembershipErrors"/>.</para>
/// </remarks>
public interface IOrgInvitationService
{
    Task<OrgInvitationSent> CreateAsync(CreateOrgInvitation request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrgInvitationView>> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<OrgInvitationSent> ResendAsync(Guid orgId, Guid invitationId, string actorUserId, CancellationToken cancellationToken = default);

    Task RevokeAsync(Guid orgId, Guid invitationId, string actorUserId, CancellationToken cancellationToken = default);

    Task<OrgInvitationLink> CopyLinkAsync(Guid orgId, Guid invitationId, string actorUserId, CancellationToken cancellationToken = default);

    Task<OrgInvitationPreview?> LookupAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts the invitation for a signed-in account: the account becomes a member of the org with the invited role, areas
    /// and scope. The account must have the invited email, verified, and must not be a platform admin. A person with no org
    /// joins at once; one with an empty and unbilled org of its own leaves it; anyone else is refused (no merging).
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainGoneException">
    /// <see cref="OrgInvitationErrors.Invalid"/>, <see cref="OrgInvitationErrors.Expired"/>,
    /// <see cref="OrgInvitationErrors.Used"/> or <see cref="OrgInvitationErrors.Revoked"/>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException">
    /// <see cref="OrgInvitationErrors.EmailMismatch"/>, <see cref="OrgInvitationErrors.EmailNotVerified"/> or
    /// <see cref="OrgInvitationErrors.PlatformAdmin"/>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// <see cref="OrgInvitationErrors.UserHasOrganization"/>, or the account already is a member of the org
    /// (<see cref="OrgMembershipErrors.AlreadyMember"/>).
    /// </exception>
    Task<OrgInvitationAccepted> AcceptAsync(AcceptOrgInvitation request, CancellationToken cancellationToken = default);
}
