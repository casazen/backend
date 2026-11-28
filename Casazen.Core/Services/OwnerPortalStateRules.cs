using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public sealed record OwnerInvitationFact(Guid Id, OwnerInvitationStatus Status, DateTime ExpiresAt, DateTime CreatedAt);

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

    public static bool CanBeInvited(string state) =>
        state is OwnerPortalStates.Ready or OwnerPortalStates.Expired or OwnerPortalStates.Revoked;
}
