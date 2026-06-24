using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id);
    Task<Payment?> GetByTransactionIdAsync(string transactionId);
    Task<IEnumerable<Payment>> GetByBookingAsync(Guid bookingId);
    Task<IEnumerable<Payment>> GetByPropertyAsync(Guid propertyId);
    Task<IEnumerable<Payment>> GetAllAsync();
    Task<Payment> AddAsync(Payment payment);
    Task<Payment> UpdateAsync(Payment payment);
    Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate);
}