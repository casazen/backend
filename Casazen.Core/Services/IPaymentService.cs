using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPaymentService
{
    Task<Payment?> GetPaymentAsync(Guid id);
    Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate);
}
