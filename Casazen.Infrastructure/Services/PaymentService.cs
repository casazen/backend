using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

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