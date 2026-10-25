namespace Casazen.Core.Services;

/// <summary>Error codes for owner invitation operations.</summary>
public static class OwnerInvitationErrors
{
    public const string Invalid = "owner_invitation_invalid";
    public const string Expired = "owner_invitation_expired";
    public const string Used = "owner_invitation_used";
    public const string Revoked = "owner_invitation_revoked";
    public const string EmailMismatch = "owner_invitation_email_mismatch";
    public const string NotFound = "owner_invitation_not_found";
    public const string DailyLimitReached = "owner_invitation_daily_limit_reached";
    public const string PropertyOwnerNotFound = "property_owner_not_found";
}

/// <summary>
/// Invitation of property owners to the owner portal (ADR-011).
/// Creates invitation with token, sends email, links Auth0 user to OwnerUser on acceptance.
/// </summary>
/// <remarks>
/// Rate limit: 100 invitations/day/org (ADR-011-R4).
/// Acceptance is idempotent: duplicate clicks do not fail (ADR-011-R10).
/// </remarks>
public interface IOwnerInvitationService
{
    /// <summary>
    /// Creates an invitation for a property owner: generates token, sends email.
    /// Rate limited to 100 invitations per day per org.
    /// </summary>
    /// <param name="orgId">The agency org sending the invitation.</param>
    /// <param name="propertyOwnerId">The PropertyOwner being invited.</param>
    /// <param name="actorUserId">The user sending the invitation (User.Id).</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The created invitation.</returns>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">PropertyOwner not found or not in this org.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">Daily rate limit reached (100/day/org).</exception>
    Task<Guid> CreateInvitationAsync(
        Guid orgId,
        Guid propertyOwnerId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an invitation: links Auth0 user to OwnerUser (creates if not exists).
    /// Idempotent: duplicate acceptance of the same invitation does not fail.
    /// </summary>
    /// <param name="token">The secret token from the invitation link.</param>
    /// <param name="auth0Sub">The Auth0 user ID (sub claim).</param>
    /// <param name="email">The email from Auth0.</param>
    /// <param name="firstName">Owner first name.</param>
    /// <param name="lastName">Owner last name.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The OwnerUser ID.</returns>
    /// <exception cref="Casazen.Core.Exceptions.DomainGoneException">Invalid, expired, used, or revoked invitation.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException">Email mismatch.</exception>
    Task<Guid> AcceptInvitationAsync(
        string token,
        string auth0Sub,
        string email,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default);
}
