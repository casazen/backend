using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) of the platform admin's supplier management (SU-12, A4-29) and the
/// SharedResources keys of their messages.
/// </summary>
public static class SupplierAdminErrorCodes
{
    /// <summary>404: no supplier profile with that org id.</summary>
    public const string SupplierNotFound = "supplier_not_found";

    /// <summary>409: the supplier is already suspended.</summary>
    public const string AlreadySuspended = "supplier_already_suspended";

    /// <summary>409: the supplier is not suspended, so there is nothing to reactivate.</summary>
    public const string NotSuspended = "supplier_not_suspended";

    /// <summary>404: no invite with that id.</summary>
    public const string InviteNotFound = "supplier_invite_not_found";

    /// <summary>409: only a pending invite (not used, revoked or expired) can be revoked.</summary>
    public const string InviteNotPending = "supplier_invite_not_pending";

    /// <summary>409: only a pending or expired invite can be sent again (not a used or revoked one).</summary>
    public const string InviteNotResendable = "supplier_invite_not_resendable";

    /// <summary>409: another invite for the same email is pending, so this one cannot be sent again.</summary>
    public const string DuplicateInvite = "duplicate_invite";

    public const string SupplierNotFoundMessageKey = "SupplierProfileNotFound";
    public const string AlreadySuspendedMessageKey = "SupplierAlreadySuspended";
    public const string NotSuspendedMessageKey = "SupplierNotSuspended";
    public const string InviteNotFoundMessageKey = "SupplierInviteNotFound";
    public const string InviteNotPendingMessageKey = "SupplierInviteNotPending";
    public const string InviteNotResendableMessageKey = "SupplierInviteNotResendable";
    public const string DuplicateInviteMessageKey = "SupplierInviteDuplicate";
}

/// <summary>Where an invite stands (SU-12), computed on read from its columns and the clock, never stored.</summary>
public enum SupplierInviteState
{
    /// <summary>Not used, not revoked, not expired: the link works.</summary>
    Pending,

    /// <summary>Accepted by the invited supplier.</summary>
    Used,

    /// <summary>
    /// Past its expiry, or created before SU-01 (no token hash: its link can no longer be accepted); it can be sent
    /// again.
    /// </summary>
    Expired,

    /// <summary>Revoked by an admin.</summary>
    Revoked,
}

/// <summary>Filters and page of the admin list of suppliers.</summary>
/// <param name="Search">Case-insensitive part of the legal name or the email.</param>
/// <param name="Status">Only suppliers in this status.</param>
public record AdminSupplierListQuery(string? Search, SupplierStatus? Status, int Page, int PageSize);

/// <summary>A supplier as the platform admin sees it.</summary>
/// <param name="OpenRequests">Requests of the supplier still to be worked (new, taken, in progress).</param>
public record AdminSupplierItem(
    Guid OrgId,
    string LegalName,
    string Email,
    string Phone,
    SupplierStatus Status,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Comuni,
    DateTime CreatedAt,
    DateTime? SuspendedAt,
    string? SuspensionReason,
    int OpenRequests);

/// <summary>Filters and page of the admin list of invites.</summary>
public record AdminInviteListQuery(string? Search, SupplierInviteState? State, int Page, int PageSize);

/// <summary>An invite as the platform admin sees it. The link token is never part of it (only its hash is stored).</summary>
public record AdminInviteItem(
    Guid Id,
    string Email,
    string ComuneCode,
    IReadOnlyList<string> Categories,
    string? Message,
    SupplierInviteState State,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt);

/// <summary>One line of the audit trail of the supplier admin actions. <paramref name="ActorName"/> is null when the admin is unknown.</summary>
public record SupplierAdminAuditItem(
    Guid Id,
    SupplierAdminAuditAction Action,
    Guid? SupplierOrgId,
    Guid? InviteId,
    string ActorUserId,
    string? ActorName,
    DateTime OccurredAt,
    string? Reason,
    SupplierStatus? PreviousStatus,
    SupplierStatus? NewStatus);

/// <summary>
/// Platform admin management of the supplier marketplace (SU-12, A4-29): who the suppliers are, suspension and
/// reactivation with an audit trail, and the invites. Who may call it (<c>AdminOnly</c>) is decided by the web layer;
/// the actor id is only recorded.
/// </summary>
/// <remarks>
/// A suspended supplier receives no new request (<see cref="ServiceRequestErrorCodes.SupplierInactive"/>), cannot take,
/// complete or reject one (<see cref="ServiceRequestErrorCodes.SupplierNotActive"/>) and cannot reactivate itself with
/// the activation wizard. Its open requests stay as they are. Errors: <see cref="Casazen.Core.Exceptions.NotFoundException"/>
/// (404), <see cref="Casazen.Core.Exceptions.DomainConflictException"/> (409), codes of <see cref="SupplierAdminErrorCodes"/>.
/// </remarks>
public interface ISupplierAdminService
{
    /// <summary>A page of the supplier profiles matching <paramref name="query"/>, newest first, paginated in SQL, and their total.</summary>
    Task<(IReadOnlyList<AdminSupplierItem> Items, int Total)> ListAsync(
        AdminSupplierListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspends a pending or active supplier (409 <see cref="SupplierAdminErrorCodes.AlreadySuspended"/> if it already
    /// is) and records <paramref name="reason"/> and <paramref name="actorUserId"/> in the audit trail.
    /// </summary>
    Task<AdminSupplierItem> SuspendAsync(
        Guid orgId,
        string actorUserId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reactivates a suspended supplier (409 <see cref="SupplierAdminErrorCodes.NotSuspended"/> otherwise): back to
    /// <see cref="SupplierStatus.Active"/> when it had accepted the terms (it was active before), otherwise to
    /// <see cref="SupplierStatus.Pending"/>: a reactivation never skips the activation wizard.
    /// </summary>
    Task<AdminSupplierItem> ReactivateAsync(
        Guid orgId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>The audit trail of one supplier, newest first (<c>NotFound</c> when the supplier does not exist).</summary>
    Task<IReadOnlyList<SupplierAdminAuditItem>> GetAuditAsync(
        Guid orgId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>A page of the invites matching <paramref name="query"/>, newest first, paginated in SQL, and their total.</summary>
    Task<(IReadOnlyList<AdminInviteItem> Items, int Total)> ListInvitesAsync(
        AdminInviteListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a pending or expired invite again: a new link token (the old link stops working), a new 7-day expiry and
    /// the invite email queued on Hangfire. 409 <see cref="SupplierAdminErrorCodes.InviteNotResendable"/> for a used or
    /// revoked invite, <see cref="SupplierAdminErrorCodes.DuplicateInvite"/> when another invite for the email is
    /// pending, <c>supplier_email_taken</c> when a profile with the email exists since.
    /// </summary>
    Task<SupplierInvite> ResendInviteAsync(
        Guid inviteId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes a pending invite: its link no longer works (422 <c>supplier_invite_revoked</c> when used).</summary>
    Task RevokeInviteAsync(
        Guid inviteId,
        string actorUserId,
        CancellationToken cancellationToken = default);
}
