using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who is calling. <see cref="CanReadLease"/> and <see cref="CanManageLease"/> come from the lease permissions
/// (<c>lease.read</c>, <c>lease.create</c>) on this lease's property. The tenant is recognised by a verified
/// email equal to a tenant party of the lease, and does not need those permissions to open or read a request.
/// </summary>
public sealed record LeaseMaintenanceCaller(
    Guid OrgId,
    string UserId,
    string? Email,
    bool EmailVerified,
    bool CanReadLease,
    bool CanManageLease,
    bool CanDebitOrg);

public sealed record LeaseMaintenancePhotoContent(string FileName, byte[] Content);

public sealed record CreateLeaseMaintenanceCommand(
    string Title,
    string Description,
    LeaseMaintenanceUrgency Urgency,
    IReadOnlyList<LeaseMaintenancePhotoContent> Photos);

public sealed record LeaseMaintenanceDecisionCommand(LeaseMaintenanceDecision Decision, string? Reason, long? AmountCents = null);

public sealed record LeaseMaintenancePaymentCommand(long AmountCents);

public sealed record LeaseMaintenanceAccess(Guid LeaseId, Guid OrgId, Guid PropertyId, string? PropertyOwnerId);

public sealed record LeaseMaintenancePhotoView(Guid Id, string FileName, string ContentType);

public sealed record LeaseMaintenancePhotoFile(string FileName, string ContentType, byte[] Content);

public sealed record LeaseMaintenanceRequestView(
    Guid Id,
    Guid LeaseId,
    string Title,
    string Description,
    LeaseMaintenanceUrgency Urgency,
    LeaseMaintenanceStatus Status,
    string? RejectionReason,
    bool Paid,
    long? AmountCents,
    string Currency,
    IReadOnlyList<LeaseMaintenancePhotoView> Photos,
    DateTime CreatedAt,
    DateTime? DecidedAt);

public sealed record LeaseMaintenanceListView(IReadOnlyList<LeaseMaintenanceRequestView> Items);

public sealed record LeaseMaintenancePaymentView(
    Guid RequestId,
    LeaseMaintenanceStatus Status,
    bool Paid,
    Guid PaymentId,
    string PaymentIntentId,
    string? ClientSecret,
    long AmountCents,
    string Currency,
    string PaymentStatus);

public interface ILeaseMaintenanceService : ILeaseMaintenancePaymentApplier
{
    Task<LeaseMaintenanceAccess?> FindLeaseAsync(Guid orgId, Guid leaseId, CancellationToken cancellationToken = default);

    Task<LeaseMaintenanceRequestView> CreateAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, CreateLeaseMaintenanceCommand command, CancellationToken cancellationToken = default);

    Task<LeaseMaintenanceListView> ListAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, CancellationToken cancellationToken = default);

    Task<LeaseMaintenancePhotoFile> ReadPhotoAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, Guid requestId, Guid photoId, CancellationToken cancellationToken = default);

    Task<LeaseMaintenanceRequestView> DecideAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, Guid requestId, LeaseMaintenanceDecisionCommand command, CancellationToken cancellationToken = default);

    Task<LeaseMaintenancePaymentView> PayAsync(
        LeaseMaintenanceCaller caller,
        Guid leaseId,
        Guid requestId,
        LeaseMaintenancePaymentCommand command,
        BankLinkCaller bankCaller,
        CancellationToken cancellationToken = default);
}

public interface ILeaseMaintenancePaymentApplier
{
    Task MarkPaidFromBankAsync(Guid orgId, Guid requestId, long amountCents, CancellationToken cancellationToken = default);
}
