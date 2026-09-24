using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id);
    Task<Payment?> GetByTransactionIdAsync(string transactionId);
    Task<IEnumerable<Payment>> GetByBookingAsync(Guid bookingId);
    Task<IEnumerable<Payment>> GetByPropertyAsync(Guid propertyId);
    /// <summary>Payments of the host scope (org, and the owner's properties when set), newest first; filtered in SQL.</summary>
    Task<IEnumerable<Payment>> GetByScopeAsync(HostScope scope);
    Task<Payment> AddAsync(Payment payment);
    Task<Payment> UpdateAsync(Payment payment);
    Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate);
}