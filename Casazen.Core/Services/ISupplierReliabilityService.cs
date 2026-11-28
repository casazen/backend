using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The reliability of the suppliers (SP-REL): writes the events that weigh on it and reads what they add up to for the ranking
/// (<c>SupplierRanking</c>). It is a record, not a judge: nothing here refuses or blocks a supplier, and a failure to record an event is
/// logged and never reaches the caller, whose own work is already done. Runbook <c>docs/runbooks/suppliers.md</c>.
/// </summary>
public interface ISupplierReliabilityService
{
    Task RecordAsync(
        Guid supplierOrgId,
        SupplierReliabilityEventKind kind,
        Guid? requestId,
        DateTime occurredAt,
        bool asBackup = false,
        string? detail = null,
        CancellationToken cancellationToken = default);

    Task RecordReplyAsync(
        Guid supplierOrgId,
        Guid requestId,
        DateTime repliedAt,
        DateTime? dueAt,
        bool asBackup = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier did not answer a request in time: it loses points (<see cref="SupplierReliabilityEventKind.NoReplyInTime"/>), unless it
    /// could not be reached while the request waited, between <paramref name="windowStartUtc"/> and <paramref name="dueAtUtc"/>: not
    /// <c>Active</c>, on time off, or with the whole window outside its working hours. True when the points were taken (or had been): the
    /// supplier can be told why; false when it is excused.
    /// </summary>
    Task<bool> RecordNoReplyAsync(
        Guid supplierOrgId,
        Guid requestId,
        DateTime windowStartUtc,
        DateTime dueAtUtc,
        bool asBackup = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, int>> PointsAsync(
        IReadOnlyCollection<Guid> supplierOrgIds,
        CancellationToken cancellationToken = default);
}
