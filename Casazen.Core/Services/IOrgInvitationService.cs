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
/// <param name="PropertyScope">All the properties or only some (the list of them is AM-03).</param>
/// <param name="Language">Language of the emails: <c>it</c> (default) or <c>en</c>.</param>
public sealed record CreateOrgInvitation(
    Guid OrgId,
    string ActorUserId,
    string Email,
    string Name,
    OrgRole Role,
    IReadOnlyCollection<string> Areas,
    PropertyScope PropertyScope = PropertyScope.All,
    string? Language = null);

/// <summary>
/// An invitation as the team page shows it: never the token or its hash. <paramref name="Status"/> is the <b>effective</b>
/// state: <see cref="OrgInvitationStatus.Pending"/> while it can still be accepted, <see cref="OrgInvitationStatus.Expired"/>
/// once its expiry has passed (also before the job marks it).
/// </summary>
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
    string InvitedByUserId);

/// <summary>
/// The invitation after it was created or sent again. <paramref name="EmailQueued"/> is false when the email could not be
/// queued (provider not configured, queue down): the invitation exists and its link can be copied
/// (<see cref="IOrgInvitationService.CopyLinkAsync"/>) or sent again.
/// </summary>
public sealed record OrgInvitationSent(OrgInvitationView Invitation, bool EmailQueued);

/// <summary>
/// A fresh link for an invitation (the one the person opens to accept). The link carries the secret token, so the previous
/// link, the one of the email included, stops working the moment this one is issued.
/// </summary>
public sealed record OrgInvitationLink(Guid InvitationId, string Url, DateTime ExpiresAt);

/// <summary>What the person sees before accepting (<see cref="IOrgInvitationService.LookupAsync"/>).</summary>
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

/// <summary>
/// The person is now a member. <paramref name="LeftEmptyOrg"/> is true when it had an automatically created, empty and
/// unbilled org of its own and left it for this one.
/// </summary>
public sealed record OrgInvitationAccepted(
    Guid OrgId,
    string OrgName,
    OrgRole Role,
    IReadOnlyList<string> Areas,
    bool LeftEmptyOrg);

/// <summary>Stable codes of the refusals of <see cref="IOrgInvitationService"/>, translated by the clients.</summary>
public static class OrgInvitationErrors
{
    // 410 Gone: the link cannot be used any more.

    /// <summary>The link is not an invitation we know (malformed, unknown, or replaced by a newer link). The only answer of the anonymous lookup.</summary>
    public const string Invalid = "invitation_invalid";

    public const string Expired = "invitation_expired";
    public const string Used = "invitation_used";
    public const string Revoked = "invitation_revoked";

    // 403: the signed-in account is not the one the invitation is for.

    /// <summary>The account email is not the invited one.</summary>
    public const string EmailMismatch = "invitation_email_mismatch";

    /// <summary>The account email is not verified (Auth0): anyone could have typed it.</summary>
    public const string EmailNotVerified = "invitation_email_not_verified";

    /// <summary>A platform admin (CasaZen staff) does not join a customer's org.</summary>
    public const string PlatformAdmin = "invitation_platform_admin";

    /// <summary>403: only the owner creates and manages administrators.</summary>
    public const string OwnerRequired = "org_owner_required";

    // 409: the state of the org or of the account does not allow it.

    /// <summary>The account already belongs to an org that is not empty and unbilled (or is a member of another org): no merging.</summary>
    public const string UserHasOrganization = "invitation_user_has_organization";

    /// <summary>There already is a pending invitation for that email in the org.</summary>
    public const string AlreadyPending = "org_invitation_already_pending";

    /// <summary>The invitation is not pending any more (accepted, revoked or expired): it cannot be revoked or its link copied.</summary>
    public const string NotPending = "org_invitation_not_pending";

    // 404

    public const string NotFound = "org_invitation_not_found";
    public const string MemberNotFound = "org_member_not_found";
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
    /// <summary>
    /// Invites a person: validates, takes a seat, stores the invitation and queues the email.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// The role is owner (<see cref="OrgMembershipErrors.OwnerNotAssignable"/>) or no area was chosen
    /// (<see cref="OrgMembershipErrors.AreaRequired"/>).
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException">
    /// The inviter is not the owner or an administrator of the org, or it is not the owner and invites an administrator
    /// (<see cref="OrgInvitationErrors.OwnerRequired"/>).
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// No free seat (<see cref="OrgSeatErrors.LimitReached"/>), a pending invitation for that email already exists
    /// (<see cref="OrgInvitationErrors.AlreadyPending"/>) or the email is a member of the org
    /// (<see cref="OrgMembershipErrors.AlreadyMember"/>).
    /// </exception>
    Task<OrgInvitationSent> CreateAsync(CreateOrgInvitation request, CancellationToken cancellationToken = default);

    /// <summary>The org's open and expired invitations (the ones that can still be sent again), newest first.</summary>
    Task<IReadOnlyList<OrgInvitationView>> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the invitation again: a new token (the previous link stops working), seven more days from now, the reminder
    /// armed again. An expired invitation comes back to life and takes a seat again.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><see cref="OrgInvitationErrors.NotFound"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// Accepted or revoked (<see cref="OrgInvitationErrors.NotPending"/>); no seat for an expired one
    /// (<see cref="OrgSeatErrors.LimitReached"/>); another pending invitation for the same email
    /// (<see cref="OrgInvitationErrors.AlreadyPending"/>).
    /// </exception>
    Task<OrgInvitationSent> ResendAsync(Guid orgId, Guid invitationId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a pending invitation: its link stops working and the seat is free again.</summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><see cref="OrgInvitationErrors.NotFound"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="OrgInvitationErrors.NotPending"/>.</exception>
    Task RevokeAsync(Guid orgId, Guid invitationId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A fresh link for a pending invitation, to be sent by other means than the email ("copy link"). It rotates the token,
    /// like sending again, but changes neither the expiry nor the reminder and sends nothing.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><see cref="OrgInvitationErrors.NotFound"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="OrgInvitationErrors.NotPending"/> (also once expired: send it again).</exception>
    Task<OrgInvitationLink> CopyLinkAsync(Guid orgId, Guid invitationId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What an invitation link is for, to show before accepting. Anonymous. <c>null</c> for a link that is malformed,
    /// unknown, expired, used or revoked, <b>without telling which</b>: a stranger learns nothing from it.
    /// </summary>
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
