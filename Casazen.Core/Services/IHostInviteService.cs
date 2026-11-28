using Casazen.Core.HostInvites;

namespace Casazen.Core.Services;

public sealed record HostInviteActor(string UserId, Guid? OrgId, bool IsAdmin);

public sealed record CreateHostInvite(HostInviteActor Actor, string Email, string? Message, string? Locale);

public sealed record HostInviteSent(Guid InviteId, DateTime ExpiresAt, string? Url = null);

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

public sealed record HostInviteLookup(bool Valid, string State, string? InviterName, string? MaskedEmail, bool IsAgency = false);

public sealed record AcceptHostInvite(string? Token, string UserId, string? Email, bool EmailVerified);

public interface IHostInviteService
{
    /// <summary>
    /// Creates an invite and queues its email. Throws 409 <c>duplicate_invite</c>, 422 <c>host_invite_self</c>, 429
    /// <c>host_invite_quota_exceeded</c> / <c>host_invite_pending_limit</c> (hosts only) and, for an admin only, 409
    /// <c>host_email_taken</c>. For a host an already registered email is answered exactly like a sent invite (no account
    /// enumeration): the row is created, nothing is emailed.
    /// </summary>
    Task<HostInviteSent> CreateAsync(CreateHostInvite request, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<HostInviteItem> Items, int Total)> ListAsync(HostInviteListQuery query, CancellationToken cancellationToken = default);

    Task<HostInviteSent> ResendAsync(HostInviteActor actor, Guid inviteId, CancellationToken cancellationToken = default);

    Task RevokeAsync(HostInviteActor actor, Guid inviteId, CancellationToken cancellationToken = default);

    Task<HostInviteQuota> GetQuotaAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<HostInviteLookup> LookupAsync(string? token, CancellationToken cancellationToken = default);

    Task AcceptAsync(AcceptHostInvite request, CancellationToken cancellationToken = default);
}

public sealed record HostInviteRetentionResult(bool Skipped, int Deleted, int Erased)
{
    public static HostInviteRetentionResult SkippedRun { get; } = new(true, 0, 0);
}

public interface IHostInviteRetentionService
{
    Task<HostInviteRetentionResult> RunAsync(CancellationToken cancellationToken = default);
}
