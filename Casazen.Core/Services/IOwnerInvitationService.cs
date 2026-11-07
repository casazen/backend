using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>Error codes for owner invitation operations.</summary>
public static class OwnerInvitationErrors
{
    public const string Invalid = "owner_invitation_invalid";
    public const string Expired = "owner_invitation_expired";
    public const string Used = "owner_invitation_used";
    public const string Revoked = "owner_invitation_revoked";
    public const string EmailMismatch = "owner_invitation_email_mismatch";

    /// <summary>403: the e-mail of the account that accepts is not verified (Auth0 does not stop an unverified account from signing in).</summary>
    public const string EmailNotVerified = "owner_invitation_email_not_verified";

    public const string NotFound = "owner_invitation_not_found";
    public const string DailyLimitReached = "owner_invitation_daily_limit_reached";
    public const string PropertyOwnerNotFound = "property_owner_not_found";

    /// <summary>409: the owners changed since the preview (a recipient, an address, a state): preview again, then confirm.</summary>
    public const string PreviewStale = "owner_invitation_preview_stale";

    /// <summary>422: the confirmation names no preview (no fingerprint).</summary>
    public const string PreviewRequired = "owner_invitation_preview_required";

    /// <summary>422: an explicit list of owners that is empty. An empty list never means «all of them».</summary>
    public const string SelectionEmpty = "owner_invitation_selection_empty";

    /// <summary>422: the preview has no recipient.</summary>
    public const string NothingToSend = "owner_invitation_nothing_to_send";

    /// <summary>409: the invitation is not in force any more, so there is nothing to withdraw.</summary>
    public const string NotLive = "owner_invitation_not_live";

    /// <summary>422: the owner has no ownership or no address, so the portal has nothing to show it or nowhere to write.</summary>
    public const string OwnerNotInvitable = "owner_invitation_owner_not_invitable";

    /// <summary>422: the e-mail could not be queued (the provider is not configured, or there is no address); the invitation was withdrawn.</summary>
    public const string EmailNotQueued = "owner_invitation_email_not_queued";
}

/// <summary>
/// The invitation of the property owners of an agency to the owner portal (ADR-011, NF-109, E10/F02/US02/T02): a batch is <b>previewed</b> and then
/// <b>confirmed</b> with the fingerprint of the preview, a link is sent again or withdrawn, and the owner accepts it. <b>The owner import never
/// calls any of this</b>: an invitation exists only because a person confirmed a preview (or invited one owner).
/// </summary>
/// <remarks>
/// <para><b>States</b> (<see cref="OwnerPortalStates"/>): <c>ready</c> (nothing sent, the portal facts are complete), <c>invited</c> (a link in force),
/// <c>accepted</c>, <c>expired</c> (the link lapsed), <c>revoked</c>; worked out on every read. A resend replaces the link: the previous live one
/// becomes <c>revoked</c>, so there is never more than one link in force for an owner. Only the SHA-256 of the token is stored; the token is
/// one-time use and valid for seven days.</para>
/// <para><b>Limit.</b> At most 100 invitations a day for each org (ADR-011-R4), counted over the last 24 hours across the batches, the single
/// invitations and the resends. A batch that does not fit is refused as a whole (429): none is sent.</para>
/// <para><b>Reach.</b> The preview, the confirmation, the list, the resend and the withdrawal see only the owners inside the reach of the scope
/// (an owner of another org, one outside the reach and one that does not exist are one and the same answer).</para>
/// <para><b>Audit.</b> Every change of an invitation is a line of the activity log (<c>OwnerInvitationChanged</c>) in the unit of work of the change.</para>
/// </remarks>
public interface IOwnerInvitationService
{
    /// <summary>
    /// Creates an invitation for ONE property owner: generates the token, sends the e-mail, replaces the link in force if there is one. Rate limited to
    /// 100 invitations per day per org. The owner of another org is the same 404 as one that does not exist.
    /// </summary>
    /// <param name="orgId">The agency org sending the invitation.</param>
    /// <param name="propertyOwnerId">The PropertyOwner being invited.</param>
    /// <param name="actorUserId">The user sending the invitation (User.Id).</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The created invitation.</returns>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">PropertyOwner not found or not in this org.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainTooManyRequestsException">Daily rate limit reached (100/day/org).</exception>
    Task<Guid> CreateInvitationAsync(
        Guid orgId,
        Guid propertyOwnerId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The batch that confirming would send, sent to nobody: the owners of <paramref name="ownerIds"/> (or all of them when <c>null</c>; an empty list
    /// is 422, never «all») whose ownership and portal identity are ready and that can be invited now, the exclusions with their reasons, the quota
    /// of the day and the fingerprint the confirmation must carry.
    /// </summary>
    Task<OwnerInvitationPreview> PreviewBatchAsync(HostScope scope, IReadOnlyCollection<Guid>? ownerIds, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the batch of the preview: judges it again under the lock of the org and refuses (409 <see cref="OwnerInvitationErrors.PreviewStale"/>) when
    /// it is not the one <paramref name="fingerprint"/> names. All or nothing as to the limit of the day.
    /// </summary>
    Task<OwnerInvitationBatchResult> ConfirmBatchAsync(
        HostScope scope,
        string actorUserId,
        IReadOnlyCollection<Guid>? ownerIds,
        string? fingerprint,
        CancellationToken cancellationToken);

    /// <summary>Every owner inside the reach with the state of its invitation: <c>notReady</c>, <c>ready</c>, <c>invited</c>, <c>accepted</c>, <c>expired</c>, <c>revoked</c>.</summary>
    Task<IReadOnlyList<OwnerInvitationStatusItem>> ListAsync(HostScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the owner of the invitation a new link and withdraws the one in force (the old token stops working). An accepted invitation is 409
    /// <see cref="OwnerInvitationErrors.Used"/>. Counts for the limit of the day.
    /// </summary>
    Task<OwnerInvitationStatusItem> ResendAsync(HostScope scope, string actorUserId, Guid invitationId, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws an invitation in force: the link stops working. A second withdrawal finds it done and writes nothing; an accepted one is 409
    /// <see cref="OwnerInvitationErrors.Used"/>, a lapsed one 409 <see cref="OwnerInvitationErrors.NotLive"/>.
    /// </summary>
    Task<OwnerInvitationStatusItem> RevokeAsync(HostScope scope, string actorUserId, Guid invitationId, CancellationToken cancellationToken);

    /// <summary>
    /// The owner accepts: the token is consumed once, the cross-agency identity (<c>OwnerUser</c>) is linked or created for the account, and only the
    /// ownerships of the invitation that are not over become the account's. A repeat by the same account finds it done and answers the same; a token
    /// that lapsed, was withdrawn, was used by another account or was sent to another address gives access to nothing.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainGoneException">Invalid, expired, used or withdrawn.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException">The e-mail of the account is not verified, or is not the invited one.</exception>
    Task<OwnerInvitationAccepted> AcceptAsync(AcceptOwnerInvitation command, CancellationToken cancellationToken);
}

/// <summary>The owner who accepts: the secret of the link and the account that holds it (the e-mail comes from the token or from Auth0, never from the request).</summary>
public sealed record AcceptOwnerInvitation(string Token, string Auth0Sub, string? Email, bool EmailVerified, string FirstName, string LastName);

/// <summary>What the acceptance did, and the claims the owner portal reads (ADR-011-R7).</summary>
/// <param name="OwnerUserId">The cross-agency identity: the <c>owner_id</c> claim.</param>
/// <param name="AgencyIds">The orgs where the owner has an ownership that is not over: the <c>agency_ids</c> claim.</param>
/// <param name="PropertyIds">The properties of those ownerships: the <c>owned_property_ids</c> claim.</param>
/// <param name="AlreadyAccepted">True when the same account had accepted this invitation before.</param>
/// <param name="ClaimsRefresh"><see cref="OwnerClaimsRefresh"/>: whether the claims were handed to the identity provider so that the next token carries them.</param>
public sealed record OwnerInvitationAccepted(
    Guid OwnerUserId,
    IReadOnlyList<Guid> AgencyIds,
    IReadOnlyList<Guid> PropertyIds,
    bool AlreadyAccepted,
    string ClaimsRefresh);

/// <summary>Where the claims of the owner went (cross-repo contract).</summary>
public static class OwnerClaimsRefresh
{
    /// <summary>Written to the account at the identity provider: the next token (ask for a new one) carries them.</summary>
    public const string Published = "published";

    /// <summary>The Management API of the identity provider is not configured: the claims are in the answer only.</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>The identity provider refused or did not answer: accepting again retries it.</summary>
    public const string Failed = "failed";
}

/// <summary>An owner the batch would invite: nothing is sent by the preview.</summary>
public sealed record OwnerInvitationRecipient(Guid OwnerId, string DisplayName, string Email, string PortalState);

/// <summary>An owner the batch leaves out, and why (<see cref="OwnerReadinessReasons"/> of the portal, or <see cref="OwnerInvitationExclusions"/>).</summary>
public sealed record OwnerInvitationExclusion(Guid OwnerId, string? DisplayName, IReadOnlyList<string> Reasons);

/// <summary>Why an owner is left out of a batch, besides the facts of the portal readiness (cross-repo contract).</summary>
public static class OwnerInvitationExclusions
{
    /// <summary>A link is in force: it is sent again (which replaces it), not invited.</summary>
    public const string AlreadyInvited = "already_invited";

    public const string AlreadyAccepted = "already_accepted";

    /// <summary>The id is not an owner of the caller (another org's, outside the reach, or unknown: one answer).</summary>
    public const string OwnerNotFound = "owner_not_found";
}

/// <summary>How many invitations the org has sent in the last 24 hours against the limit of the day.</summary>
public sealed record OwnerInvitationQuota(int Limit, int Used, int Remaining);

/// <summary>
/// The answer of the preview. <paramref name="Fingerprint"/> hashes the org, the selection, the recipients with their address and state, and the
/// exclusions with their reasons: whatever changes any of them changes it, and the confirmation of the old one is refused.
/// </summary>
public sealed record OwnerInvitationPreview(
    string Fingerprint,
    IReadOnlyList<OwnerInvitationRecipient> Recipients,
    IReadOnlyList<OwnerInvitationExclusion> Exclusions,
    OwnerInvitationQuota Quota);

/// <summary>One invitation sent by a batch.</summary>
public sealed record OwnerInvitationSent(Guid InvitationId, Guid OwnerId, DateTime ExpiresAt);

/// <summary>An owner of the batch whose e-mail could not be queued: its invitation was withdrawn.</summary>
public sealed record OwnerInvitationFailure(Guid OwnerId, string Code);

/// <summary>The answer of the confirmation.</summary>
public sealed record OwnerInvitationBatchResult(
    int Sent,
    IReadOnlyList<OwnerInvitationSent> Invitations,
    IReadOnlyList<OwnerInvitationFailure> Failed);

/// <summary>An owner and where its invitation stands (<see cref="OwnerPortalStates"/>); the dates are those of the latest invitation.</summary>
public sealed record OwnerInvitationStatusItem(
    Guid OwnerId,
    string DisplayName,
    string State,
    Guid? InvitationId,
    DateTime? SentAt,
    DateTime? ExpiresAt,
    DateTime? AcceptedAt,
    DateTime? ClosedAt);
