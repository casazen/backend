using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Reads and host-recorded payments. Money moves only through Stripe: refunds are <see cref="IPaymentRefundService"/>
/// (BK-02); the simulated "process payment" that marked a payment Completed without Stripe was removed (A9-15).
/// </summary>
public class PaymentService(IPaymentRepository repository) : IPaymentService
{
    public async Task<Payment?> GetPaymentAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        return await repository.GetTotalRevenueAsync(propertyId, startDate, endDate);
    }
}