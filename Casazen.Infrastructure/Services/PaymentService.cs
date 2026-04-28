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

    public async Task<Payment> ProcessPaymentAsync(Guid paymentId)
    {
        var payment = await repository.GetByIdAsync(paymentId);
        if (payment == null)
            throw new KeyNotFoundException($"Payment {paymentId} not found");

        if (payment.Status == PaymentStatus.Completed)
            throw new InvalidOperationException("Payment already processed");

        // TODO: Implement actual Stripe integration
        // For now, just update status
        payment.Status = PaymentStatus.Processing;
        payment.ProcessedAt = DateTime.UtcNow;
        await repository.UpdateAsync(payment);

        // Simulate successful payment
        payment.Status = PaymentStatus.Completed;
        await repository.UpdateAsync(payment);

        logger.LogInformation("Payment {Id} processed", paymentId);
        return payment;
    }

    public async Task<Payment> RefundPaymentAsync(Guid paymentId, decimal? amount = null)
    {
        var payment = await repository.GetByIdAsync(paymentId);
        if (payment == null)
            throw new KeyNotFoundException($"Payment {paymentId} not found");

        if (payment.Status != PaymentStatus.Completed && payment.Status != PaymentStatus.PartiallyRefunded)
            throw new InvalidOperationException("Can only refund completed or partially refunded payments");

        // Default to full refund if no amount specified
        var refundAmount = amount ?? (payment.Amount - payment.RefundedAmount);

        // Validate refund amount
        if (refundAmount <= 0)
            throw new InvalidOperationException("Refund amount must be greater than zero");

        var remainingAmount = payment.Amount - payment.RefundedAmount;
        if (refundAmount > remainingAmount)
            throw new InvalidOperationException(
                $"Cannot refund €{refundAmount}. Only €{remainingAmount} remaining (already refunded €{payment.RefundedAmount} of €{payment.Amount})");

        // TODO: Implement actual Stripe refund
        // For now, just update status and tracking

        payment.RefundedAmount += refundAmount;

        // Update status based on total refunded
        if (payment.RefundedAmount >= payment.Amount)
        {
            payment.Status = PaymentStatus.Refunded;
        }
        else
        {
            payment.Status = PaymentStatus.PartiallyRefunded;
        }

        await repository.UpdateAsync(payment);

        logger.LogInformation(
            "Payment {Id} refunded €{Amount}. Total refunded: €{TotalRefunded} of €{OriginalAmount}",
            paymentId, refundAmount, payment.RefundedAmount, payment.Amount);

        return payment;
    }

    public async Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        return await repository.GetTotalRevenueAsync(propertyId, startDate, endDate);
    }
}