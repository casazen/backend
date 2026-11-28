using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

public static class OwnerInvitationErrors
{
    public const string Invalid = "owner_invitation_invalid";
    public const string Expired = "owner_invitation_expired";
    public const string Used = "owner_invitation_used";
    public const string Revoked = "owner_invitation_revoked";
    public const string EmailMismatch = "owner_invitation_email_mismatch";

    public const string EmailNotVerified = "owner_invitation_email_not_verified";

    public const string NotFound = "owner_invitation_not_found";
    public const string DailyLimitReached = "owner_invitation_daily_limit_reached";
    public const string PropertyOwnerNotFound = "property_owner_not_found";

    /// <summary>409: the owners changed since the preview (a recipient, an address, a state): preview again, then confirm.</summary>
    public const string PreviewStale = "owner_invitation_preview_stale";

    public const string PreviewRequired = "owner_invitation_preview_required";

    public const string SelectionEmpty = "owner_invitation_selection_empty";

    public const string NothingToSend = "owner_invitation_nothing_to_send";

    public const string NotLive = "owner_invitation_not_live";

    public const string EmailNotQueued = "owner_invitation_email_not_queued";

    public const string SharedOutsideReach = "owner_shared_outside_reach";
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
    /// <param name="scope">The reach of the caller: with a partial one, an owner that holds nothing inside it is the same 404 as one that does not exist.</param>
    /// <returns>The created invitation.</returns>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">PropertyOwner not found or not in this org.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainTooManyRequestsException">Daily rate limit reached (100/day/org).</exception>
    Task<Guid> CreateInvitationAsync(
        Guid orgId,
        Guid propertyOwnerId,
        string actorUserId,
        CancellationToken cancellationToken = default,
        HostScope? scope = null);

    /// <summary>
    /// The batch that confirming would send, sent to nobody: the owners of <paramref name="ownerIds"/> (or all of them when <c>null</c>; an empty list
    /// is 422, never «all») whose ownership and portal identity are ready and that can be invited now, the exclusions with their reasons, the quota
    /// of the day and the fingerprint the confirmation must carry.
    /// </summary>
    Task<OwnerInvitationPreview> PreviewBatchAsync(HostScope scope, IReadOnlyCollection<Guid>? ownerIds, CancellationToken cancellationToken);

    Task<OwnerInvitationBatchResult> ConfirmBatchAsync(
        HostScope scope,
        string actorUserId,
        IReadOnlyCollection<Guid>? ownerIds,
        string? fingerprint,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OwnerInvitationStatusItem>> ListAsync(HostScope scope, CancellationToken cancellationToken);

    Task<OwnerInvitationStatusItem> ResendAsync(HostScope scope, string actorUserId, Guid invitationId, CancellationToken cancellationToken);

    Task<OwnerInvitationStatusItem> RevokeAsync(HostScope scope, string actorUserId, Guid invitationId, CancellationToken cancellationToken);

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

public static class OwnerClaimsRefresh
{
    public const string Published = "published";

    public const string NotConfigured = "not_configured";

    /// <summary>The identity provider refused or did not answer: accepting again retries it.</summary>
    public const string Failed = "failed";
}

public sealed record OwnerInvitationRecipient(Guid OwnerId, string DisplayName, string Email, string PortalState);

public sealed record OwnerInvitationExclusion(Guid OwnerId, string? DisplayName, IReadOnlyList<string> Reasons);

public static class OwnerInvitationExclusions
{
    public const string AlreadyInvited = "already_invited";

    public const string AlreadyAccepted = "already_accepted";

    public const string OwnerNotFound = "owner_not_found";

    public const string SharedOutsideReach = "owner_shared_outside_reach";
}

public sealed record OwnerInvitationQuota(int Limit, int Used, int Remaining);

public sealed record OwnerInvitationPreview(
    string Fingerprint,
    IReadOnlyList<OwnerInvitationRecipient> Recipients,
    IReadOnlyList<OwnerInvitationExclusion> Exclusions,
    OwnerInvitationQuota Quota);

public sealed record OwnerInvitationSent(Guid InvitationId, Guid OwnerId, DateTime ExpiresAt);

public sealed record OwnerInvitationFailure(Guid OwnerId, string Code);

public sealed record OwnerInvitationBatchResult(
    int Sent,
    IReadOnlyList<OwnerInvitationSent> Invitations,
    IReadOnlyList<OwnerInvitationFailure> Failed);

public sealed record OwnerInvitationStatusItem(
    Guid OwnerId,
    string DisplayName,
    string State,
    Guid? InvitationId,
    DateTime? SentAt,
    DateTime? ExpiresAt,
    DateTime? AcceptedAt,
    DateTime? ClosedAt);
