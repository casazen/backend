using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPaymentService
{
    Task<Payment?> GetPaymentAsync(Guid id);
    Task<IEnumerable<Payment>> GetAllPaymentsAsync();
    Task<IEnumerable<Payment>> GetBookingPaymentsAsync(Guid bookingId);
    Task<Payment> CreatePaymentAsync(Payment payment);
    Task<Payment> ProcessPaymentAsync(Guid paymentId);
    Task<Payment> RefundPaymentAsync(Guid paymentId, decimal? amount = null);
    Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate);
}
