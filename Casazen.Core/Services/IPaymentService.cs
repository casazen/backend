using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPaymentService
{
    Task<Payment?> GetPaymentAsync(Guid id);
    /// <summary>Payments visible in the host scope (org and, when set, the owner's properties), filtered in SQL.</summary>
    Task<IEnumerable<Payment>> GetPaymentsAsync(HostScope scope);
    Task<IEnumerable<Payment>> GetPropertyPaymentsAsync(Guid propertyId);
    Task<IEnumerable<Payment>> GetBookingPaymentsAsync(Guid bookingId);
    Task<Payment> CreatePaymentAsync(Payment payment);
    Task<Payment> ProcessPaymentAsync(Guid paymentId);
    Task<Payment> RefundPaymentAsync(Guid paymentId, decimal? amount = null);
    Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate);
}
