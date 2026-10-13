namespace Casazen.Core.HostInvites;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) of the host invites and the <c>SharedResources</c> keys of their messages.
/// </summary>
public static class HostInviteErrors
{
    /// <summary>404: no invite with that id (or, for a host, none of its own).</summary>
    public const string NotFound = "host_invite_not_found";

    /// <summary>409: only a pending invite can be revoked.</summary>
    public const string NotPending = "host_invite_not_pending";

    /// <summary>409: only a pending or expired invite can be sent again.</summary>
    public const string NotResendable = "host_invite_not_resendable";

    /// <summary>409: the inviter already has a pending invite for the email. Same code as the supplier invites.</summary>
    public const string Duplicate = "duplicate_invite";

    /// <summary>409, admin only: the email already belongs to a registered account. Hosts get no such answer (see the service).</summary>
    public const string EmailTaken = "host_email_taken";

    /// <summary>422: the email is empty, too long or not an address (the API boundary rejects it first with 400).</summary>
    public const string EmailInvalid = "host_invite_email_invalid";

    /// <summary>422: nobody invites its own address.</summary>
    public const string Self = "host_invite_self";

    /// <summary>429: the daily quota of the host org is used up.</summary>
    public const string QuotaExceeded = "host_invite_quota_exceeded";

    /// <summary>429: the host org already has the maximum of pending invites.</summary>
    public const string PendingLimit = "host_invite_pending_limit";

    /// <summary>422: the token is malformed or matches no invite (one answer for both).</summary>
    public const string Invalid = "host_invite_invalid";

    /// <summary>422: the invite is past its expiry.</summary>
    public const string Expired = "host_invite_expired";

    /// <summary>422: the invite was revoked.</summary>
    public const string Revoked = "host_invite_revoked";

    /// <summary>409: the invite was already accepted by another account.</summary>
    public const string Used = "host_invite_used";

    /// <summary>422: the account's email is not verified, so it cannot take an invite bound to an address.</summary>
    public const string EmailNotVerified = "host_invite_email_not_verified";

    /// <summary>422: the account's email is not the invited one.</summary>
    public const string EmailMismatch = "host_invite_email_mismatch";

    public const string NotFoundMessageKey = "HostInviteNotFound";
    public const string NotPendingMessageKey = "HostInviteNotPending";
    public const string NotResendableMessageKey = "HostInviteNotResendable";
    public const string DuplicateMessageKey = "HostInviteDuplicate";
    public const string EmailTakenMessageKey = "HostInviteEmailTaken";
    public const string EmailInvalidMessageKey = "HostInviteEmailInvalid";
    public const string SelfMessageKey = "HostInviteSelf";
    public const string QuotaExceededMessageKey = "HostInviteQuotaExceeded";
    public const string PendingLimitMessageKey = "HostInvitePendingLimit";
    public const string InvalidMessageKey = "HostInviteInvalid";
    public const string ExpiredMessageKey = "HostInviteExpired";
    public const string RevokedMessageKey = "HostInviteRevoked";
    public const string UsedMessageKey = "HostInviteUsed";
    public const string EmailNotVerifiedMessageKey = "HostInviteEmailNotVerified";
    public const string EmailMismatchMessageKey = "HostInviteEmailMismatch";
}
