using Casazen.Core.Entities;
using Casazen.Core.OrgTeam;
using Casazen.Core.Suppliers;

namespace Casazen.Core.HostInvites;

public enum HostInviteState
{
    Pending,

    Used,

    Expired,

    Revoked,
}

public static class HostInviteRules
{
    public static TimeSpan Validity => SupplierInviteTokens.Validity;

    public static readonly TimeSpan QuotaWindow = TimeSpan.FromHours(24);

    public static string NormalizeEmail(string? email) => OrgInvitationRules.NormalizeEmail(email);

    /// <summary>The language to use for <paramref name="locale"/>: itself when supported (any case), Italian otherwise.</summary>
    public static string NormalizeLocale(string? locale) => OrgInvitationRules.NormalizeLanguage(locale);

    public static HostInviteState StateOf(HostInviteRecord invite, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(invite);
        if (invite.IsUsed) return HostInviteState.Used;
        if (invite.RevokedAt is not null) return HostInviteState.Revoked;
        return invite.ExpiresAt <= now ? HostInviteState.Expired : HostInviteState.Pending;
    }

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
