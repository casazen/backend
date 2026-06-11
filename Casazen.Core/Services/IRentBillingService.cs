using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record UpsertRentScheduleRequest(
    int BillingDayOfMonth,
    decimal? Amount,
    string? Currency,
    string? MandateReference,
    bool IsActive);

public sealed class RentBillingConflictException(string message) : Exception(message);

public interface IRentBillingService
{
    Task<RentSchedule?> GetScheduleAsync(Guid leaseId, string ownerId);
    Task<RentSchedule> UpsertScheduleAsync(Guid leaseId, string ownerId, UpsertRentScheduleRequest request);
    Task<RentSchedule> DisableScheduleAsync(Guid leaseId, string ownerId);
    Task<(IReadOnlyList<RentLedgerEntry> Items, int TotalCount)> GetLedgerPageAsync(
        Guid leaseId,
        string ownerId,
        RentLedgerStatus? status,
        DateOnly? from,
        DateOnly? to,
        int page,
        int pageSize);
    Task<(Stream Content, string FileName)?> GetReceiptAsync(Guid leaseId, Guid entryId, string ownerId);
    Task MaterializeAndChargePeriodAsync(Guid scheduleId);
    Task HandleRentPaymentSucceededAsync(Guid entryId);
    Task HandleRentPaymentFailedAsync(Guid entryId, bool canceled);
}
