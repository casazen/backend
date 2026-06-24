using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class PaymentRepository(AppDbContext context) : IPaymentRepository
{
    public async Task<Payment?> GetByIdAsync(Guid id)
    {
        return await context.Payments
            .Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<Payment?> GetByTransactionIdAsync(string transactionId)
    {
        return await context.Payments
            .Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.TransactionId == transactionId);
    }

    public async Task<IEnumerable<Payment>> GetByBookingAsync(Guid bookingId)
    {
        return await context.Payments
            .Where(p => p.BookingId == bookingId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Payment>> GetByPropertyAsync(Guid propertyId)
    {
        return await context.Payments
            .Include(p => p.Booking)
            .Where(p => p.Booking!.PropertyId == propertyId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Payment>> GetAllAsync()
    {
        return await context.Payments
            .Include(p => p.Booking)
            .ToListAsync();
    }

    public async Task<Payment> AddAsync(Payment payment)
    {
        context.Payments.Add(payment);
        await context.SaveChangesAsync();
        return payment;
    }

    public async Task<Payment> UpdateAsync(Payment payment)
    {
        context.Payments.Update(payment);
        await context.SaveChangesAsync();
        return payment;
    }

    public async Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        return await context.Payments
            .Where(p => p.Booking!.PropertyId == propertyId &&
                        p.CreatedAt >= startDate &&
                        p.CreatedAt <= endDate &&
                        p.Status == PaymentStatus.Completed)
            .SumAsync(p => p.Amount);
    }
}