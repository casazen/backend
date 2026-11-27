using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public static class SupplierAdminErrorCodes
{
    public const string SupplierNotFound = "supplier_not_found";

    public const string AlreadySuspended = "supplier_already_suspended";

    public const string NotSuspended = "supplier_not_suspended";

    public const string InviteNotFound = "supplier_invite_not_found";

    public const string InviteNotPending = "supplier_invite_not_pending";

    public const string InviteNotResendable = "supplier_invite_not_resendable";

    public const string DuplicateInvite = "duplicate_invite";

    public const string SupplierNotFoundMessageKey = "SupplierProfileNotFound";
    public const string AlreadySuspendedMessageKey = "SupplierAlreadySuspended";
    public const string NotSuspendedMessageKey = "SupplierNotSuspended";
    public const string InviteNotFoundMessageKey = "SupplierInviteNotFound";
    public const string InviteNotPendingMessageKey = "SupplierInviteNotPending";
    public const string InviteNotResendableMessageKey = "SupplierInviteNotResendable";
    public const string DuplicateInviteMessageKey = "SupplierInviteDuplicate";
}

public enum SupplierInviteState
{
    Pending,

    Used,

    Expired,

    Revoked,
}

public record AdminSupplierListQuery(string? Search, SupplierStatus? Status, int Page, int PageSize);

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

public record AdminInviteListQuery(string? Search, SupplierInviteState? State, int Page, int PageSize);

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
    Task<(IReadOnlyList<AdminSupplierItem> Items, int Total)> ListAsync(
        AdminSupplierListQuery query,
        CancellationToken cancellationToken = default);

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

    Task<IReadOnlyList<SupplierAdminAuditItem>> GetAuditAsync(
        Guid orgId,
        int limit,
        CancellationToken cancellationToken = default);

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

    Task RevokeInviteAsync(
        Guid inviteId,
        string actorUserId,
        CancellationToken cancellationToken = default);
}
