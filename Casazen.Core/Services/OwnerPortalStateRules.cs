using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>An invitation of an owner, as the lifecycle reads it.</summary>
public sealed record OwnerInvitationFact(Guid Id, OwnerInvitationStatus Status, DateTime ExpiresAt, DateTime CreatedAt);

/// <summary>
/// The lifecycle of the portal invitation of an owner (E10/F02/US02/T02, NF-109, AC-NF109-4): <c>ready</c>, <c>invited</c>, <c>accepted</c>,
/// <c>expired</c>, <c>revoked</c>, and <c>notReady</c> before <c>ready</c>. Worked out on every read from the invitations and the clock, so a
/// link that lapsed is <c>expired</c> the moment it lapses, with no job to run.
/// </summary>
public static class OwnerPortalStateRules
{
    /// <summary>
    /// The state of an owner from its invitations. An accepted invitation wins whatever came after it; otherwise the latest one (by creation) says:
    /// a link still in force is <c>invited</c>, one that lapsed <c>expired</c>, one the agency withdrew <c>revoked</c>. With no invitation the owner
    /// is <c>ready</c> when <paramref name="portalReady"/> (the registry, the ownership and an e-mail address are complete), else <c>notReady</c>.
    /// </summary>
    public static string StateOf(IReadOnlyCollection<OwnerInvitationFact> invitations, DateTime nowUtc, bool portalReady)
    {
        ArgumentNullException.ThrowIfNull(invitations);
        if (invitations.Any(i => i.Status == OwnerInvitationStatus.Accepted))
            return OwnerPortalStates.Accepted;

        var latest = invitations.OrderByDescending(i => i.CreatedAt).FirstOrDefault();
        if (latest is null)
            return portalReady ? OwnerPortalStates.Ready : OwnerPortalStates.NotReady;

        return latest.Status switch
        {
            OwnerInvitationStatus.Pending => latest.ExpiresAt > nowUtc ? OwnerPortalStates.Invited : OwnerPortalStates.Expired,
            OwnerInvitationStatus.Expired => OwnerPortalStates.Expired,
            OwnerInvitationStatus.Revoked => OwnerPortalStates.Revoked,
            _ => OwnerPortalStates.NotReady,
        };
    }

    /// <summary>
    /// True when an owner in <paramref name="state"/> can be put in a batch of invitations: it is ready, or its earlier invitation lapsed or was
    /// withdrawn. An owner already <c>invited</c> has a link in force (it is resent, which replaces the link) and an <c>accepted</c> one needs none.
    /// </summary>
    public static bool CanBeInvited(string state) =>
        state is OwnerPortalStates.Ready or OwnerPortalStates.Expired or OwnerPortalStates.Revoked;
}
