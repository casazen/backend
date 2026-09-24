using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Reads and host-recorded payments. Money moves only through Stripe: refunds are <see cref="IPaymentRefundService"/>
/// (BK-02); the simulated "process payment" that marked a payment Completed without Stripe was removed (A9-15).
/// </summary>
public class PaymentService(
    IPaymentRepository repository,
    ILogger<PaymentService> logger) : IPaymentService
{
    public async Task<Payment?> GetPaymentAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Payment>> GetBookingPaymentsAsync(Guid bookingId)
    {
        return await repository.GetByBookingAsync(bookingId);
    }

    public async Task<IEnumerable<Payment>> GetPropertyPaymentsAsync(Guid propertyId)
    {
        return await repository.GetByPropertyAsync(propertyId);
    }

    public async Task<IEnumerable<Payment>> GetPaymentsAsync(HostScope scope)
    {
        return await repository.GetByScopeAsync(scope);
    }

    public async Task<Payment> CreatePaymentAsync(Payment payment)
    {
        logger.LogInformation("Creating payment for booking {BookingId}", payment.BookingId);
        return await repository.AddAsync(payment);
    }

    public async Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        return await repository.GetTotalRevenueAsync(propertyId, startDate, endDate);
    }
}