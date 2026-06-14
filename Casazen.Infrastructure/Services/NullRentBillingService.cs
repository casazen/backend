using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>Placeholder until #269 RentBillingService ships; satisfies webhook DI.</summary>
public class NullRentBillingService : IRentBillingService
{
    public Task<RentSchedule?> GetScheduleAsync(Guid leaseId, string ownerId) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task<RentSchedule> UpsertScheduleAsync(Guid leaseId, string ownerId, UpsertRentScheduleRequest request) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task<RentSchedule> DisableScheduleAsync(Guid leaseId, string ownerId) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task<(IReadOnlyList<RentLedgerEntry> Items, int TotalCount)> GetLedgerPageAsync(
        Guid leaseId, string ownerId, RentLedgerStatus? status, DateOnly? from, DateOnly? to, int page, int pageSize) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task<(Stream Content, string FileName)?> GetReceiptAsync(Guid leaseId, Guid entryId, string ownerId) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task MaterializeAndChargePeriodAsync(Guid scheduleId) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task HandleRentPaymentSucceededAsync(Guid entryId) => Task.CompletedTask;

    public Task HandleRentPaymentFailedAsync(Guid entryId, bool canceled) => Task.CompletedTask;
}
