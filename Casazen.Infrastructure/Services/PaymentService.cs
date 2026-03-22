using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

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

    public async Task<IEnumerable<Payment>> GetAllPaymentsAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<Payment> CreatePaymentAsync(Payment payment)
    {
        logger.LogInformation("Creating payment for booking {BookingId}", payment.BookingId);
        return await repository.AddAsync(payment);
    }

    public async Task<bool> ProcessPaymentAsync(Guid paymentId)
    {
        var payment = await repository.GetByIdAsync(paymentId);
        if (payment == null)
            return false;

        payment.Status = PaymentStatus.Completed;
        await repository.UpdateAsync(payment);
        logger.LogInformation("Payment {Id} processed", paymentId);
        return true;
    }

    public async Task<bool> RefundPaymentAsync(Guid paymentId, decimal? amount = null)
    {
        var payment = await repository.GetByIdAsync(paymentId);
        if (payment == null)
            return false;

        payment.Status = amount.HasValue ? PaymentStatus.PartiallyRefunded : PaymentStatus.Refunded;
        await repository.UpdateAsync(payment);
        logger.LogInformation("Payment {Id} refunded", paymentId);
        return true;
    }

    public async Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        return await repository.GetTotalRevenueAsync(propertyId, startDate, endDate);
    }
}