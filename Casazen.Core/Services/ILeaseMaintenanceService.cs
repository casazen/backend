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

/// <summary>The lease as the controller needs it to authorize the property. Null when it is not in the caller's org.</summary>
public sealed record LeaseMaintenanceAccess(Guid LeaseId, Guid OrgId, Guid PropertyId, string? PropertyOwnerId);

public sealed record LeaseMaintenancePhotoView(Guid Id, string FileName, string ContentType);

/// <summary>A private photo. The bytes are served by the authenticated endpoint. There is no public URL.</summary>
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

/// <summary>
/// A debit that was started, or the one already started. <see cref="Paid"/> is true only after Stripe reports
/// the PaymentIntent succeeded. Starting it, and declaring a manual transfer, do not set it.
/// </summary>
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

/// <summary>
/// Tenant maintenance on a lease: the tenant opens and reads, the landlord or the agency of the same org decides
/// and pays with the euro bank-link debit. Another org sees nothing.
/// </summary>
public interface ILeaseMaintenanceService : ILeaseMaintenancePaymentApplier
{
    Task<LeaseMaintenanceAccess?> FindLeaseAsync(Guid orgId, Guid leaseId, CancellationToken cancellationToken = default);

    Task<LeaseMaintenanceRequestView> CreateAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, CreateLeaseMaintenanceCommand command, CancellationToken cancellationToken = default);

    Task<LeaseMaintenanceListView> ListAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>The photo bytes for the tenant of the lease or anyone who may read it. Missing and other-org photos are not found.</summary>
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

/// <summary>What the bank-link webhook calls when a maintenance debit succeeds. Not a manual payment.</summary>
public interface ILeaseMaintenancePaymentApplier
{
    Task MarkPaidFromBankAsync(Guid orgId, Guid requestId, long amountCents, CancellationToken cancellationToken = default);
}
