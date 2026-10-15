using Casazen.Core.HostInvites;

namespace Casazen.Core.Services;

/// <summary>
/// Who acts on a host invite. <see cref="OrgId"/> is the host org of the caller (null for a platform admin, who has no cap and
/// sees every invite); a host (<see cref="IsAdmin"/> false) only ever reaches the invites it created itself.
/// </summary>
public sealed record HostInviteActor(string UserId, Guid? OrgId, bool IsAdmin);

public sealed record CreateHostInvite(HostInviteActor Actor, string Email, string? Message, string? Locale);

public sealed record HostInviteSent(Guid InviteId, DateTime ExpiresAt);

public sealed record HostInviteInviter(string Name, string? OrgName);

public sealed record HostInviteItem(
    Guid Id,
    string Email,
    HostInviteState State,
    string? Message,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? UsedAt,
    HostInviteInviter? InvitedBy);

public sealed record HostInviteListQuery(HostInviteActor Actor, string? Search, HostInviteState? State, int Page, int PageSize);

public sealed record HostInviteQuota(int Limit, int Used, int Remaining, DateTime ResetsAt);

/// <summary>What the public page of an invite link shows. Never the whole email.</summary>
public sealed record HostInviteLookup(bool Valid, string State, string? InviterName, string? MaskedEmail);

public sealed record AcceptHostInvite(string? Token, string UserId, string? Email, bool EmailVerified);

/// <summary>
/// Invitation of new independent hosts by email, by platform admins and by hosts. The invite grants nothing: it binds an email,
/// leads to the normal signup and records who invited. Rules and codes: <see cref="HostInviteErrors"/>.
/// </summary>
public interface IHostInviteService
{
    /// <summary>
    /// Creates an invite and queues its email. Throws 409 <c>duplicate_invite</c>, 422 <c>host_invite_self</c>, 429
    /// <c>host_invite_quota_exceeded</c> / <c>host_invite_pending_limit</c> (hosts only) and, for an admin only, 409
    /// <c>host_email_taken</c>. For a host an already registered email is answered exactly like a sent invite (no account
    /// enumeration): the row is created, nothing is emailed.
    /// </summary>
    Task<HostInviteSent> CreateAsync(CreateHostInvite request, CancellationToken cancellationToken = default);

    /// <summary>The invites, newest first, paginated in SQL. A host sees its own only.</summary>
    Task<(IReadOnlyList<HostInviteItem> Items, int Total)> ListAsync(HostInviteListQuery query, CancellationToken cancellationToken = default);

    /// <summary>A new link and a new expiry for a pending or expired invite; the old link stops working. Counts against the quota.</summary>
    Task<HostInviteSent> ResendAsync(HostInviteActor actor, Guid inviteId, CancellationToken cancellationToken = default);

    /// <summary>Revokes a pending invite.</summary>
    Task RevokeAsync(HostInviteActor actor, Guid inviteId, CancellationToken cancellationToken = default);

    /// <summary>The daily quota of a host org.</summary>
    Task<HostInviteQuota> GetQuotaAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>What the invite link is for. Unknown, malformed, used, expired and revoked links answer <c>Valid = false</c>.</summary>
    Task<HostInviteLookup> LookupAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The signed-in account takes the invite: its <b>verified</b> email must be the invited one. Marks the invite used; sending it
    /// again by the same account finds it done. Grants no role.
    /// </summary>
    Task AcceptAsync(AcceptHostInvite request, CancellationToken cancellationToken = default);
}

/// <summary>Result of one run of the retention of the host invites.</summary>
public sealed record HostInviteRetentionResult(bool Skipped, int Deleted, int Erased)
{
    public static HostInviteRetentionResult SkippedRun { get; } = new(true, 0, 0);
}

/// <summary>GDPR minimization of the closed host invites, run daily by <c>HostInviteRetentionJob</c>.</summary>
public interface IHostInviteRetentionService
{
    Task<HostInviteRetentionResult> RunAsync(CancellationToken cancellationToken = default);
}
