using Casazen.Core.Entities;
using Casazen.Core.OrgTeam;
using Casazen.Core.Suppliers;

namespace Casazen.Core.HostInvites;

/// <summary>State of a host invite at a given instant (computed, never stored).</summary>
public enum HostInviteState
{
    /// <summary>Not used, not revoked, not expired: the link works.</summary>
    Pending,

    /// <summary>Accepted by the invited person.</summary>
    Used,

    /// <summary>Past its expiry; it can be sent again.</summary>
    Expired,

    /// <summary>Withdrawn by the inviter or an admin.</summary>
    Revoked,
}

/// <summary>
/// The small rules of the host invites in one place, so the service, the retention and the tests read the same thing.
/// The token (generation, normalization, hash, 7-day validity) and the email normalization are the ones of the supplier and
/// org invites, not copies.
/// </summary>
public static class HostInviteRules
{
    /// <summary>How long an invite link works, from its creation or from the moment it is sent again.</summary>
    public static TimeSpan Validity => SupplierInviteTokens.Validity;

    /// <summary>The window the daily quota is counted over: the last 24 hours, rolling.</summary>
    public static readonly TimeSpan QuotaWindow = TimeSpan.FromHours(24);

    /// <summary>The form an email is stored and compared in: trimmed and lowercase.</summary>
    public static string NormalizeEmail(string? email) => OrgInvitationRules.NormalizeEmail(email);

    /// <summary>The language to use for <paramref name="locale"/>: itself when supported (any case), Italian otherwise.</summary>
    public static string NormalizeLocale(string? locale) => OrgInvitationRules.NormalizeLanguage(locale);

    /// <summary>The state of an invite at <paramref name="now"/>: the in-memory twin of <see cref="StatePredicate"/>.</summary>
    public static HostInviteState StateOf(HostInviteRecord invite, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(invite);
        if (invite.IsUsed) return HostInviteState.Used;
        if (invite.RevokedAt is not null) return HostInviteState.Revoked;
        return invite.ExpiresAt <= now ? HostInviteState.Expired : HostInviteState.Pending;
    }

    /// <summary>The SQL form of <see cref="StateOf"/>.</summary>
    public static System.Linq.Expressions.Expression<Func<HostInviteRecord, bool>> StatePredicate(HostInviteState state, DateTime now) =>
        state switch
        {
            HostInviteState.Used => i => i.IsUsed,
            HostInviteState.Revoked => i => !i.IsUsed && i.RevokedAt != null,
            HostInviteState.Expired => i => !i.IsUsed && i.RevokedAt == null && i.ExpiresAt <= now,
            _ => i => !i.IsUsed && i.RevokedAt == null && i.ExpiresAt > now,
        };

    /// <summary>Text of the email replacing the invitee's address once its personal data is erased: unique, never deliverable.</summary>
    public static string ErasedEmail(Guid inviteId) => $"erased-{inviteId:N}@invalid";
}
