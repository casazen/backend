using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IAlloggiatiWebReportRepository
{
    Task<AlloggiatiWebReport?> GetByIdAsync(Guid id);
    Task<AlloggiatiWebReport?> GetByBookingIdAsync(Guid bookingId);
    Task<IEnumerable<AlloggiatiWebReport>> GetPendingReportsAsync();
    Task<AlloggiatiWebReport> AddAsync(AlloggiatiWebReport report);
    Task<AlloggiatiWebReport> UpdateAsync(AlloggiatiWebReport report);
}
