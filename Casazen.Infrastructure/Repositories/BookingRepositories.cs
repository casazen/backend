using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class BookingRepository(AppDbContext context) : IBookingRepository
{
    public async Task<Booking?> GetByIdAsync(Guid id)
    {
        return await context.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .Include(b => b.Payments)
            .FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<IEnumerable<Booking>> GetByPropertyAsync(Guid propertyId)
    {
        return await context.Bookings
            .Where(b => b.PropertyId == propertyId)
            .Include(b => b.Guest)
            .Include(b => b.Payments)
            .OrderByDescending(b => b.CheckInDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<Booking>> GetByGuestAsync(Guid guestId)
    {
        return await context.Bookings
            .Where(b => b.GuestId == guestId)
            .Include(b => b.Property)
            .OrderByDescending(b => b.CheckInDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<Booking>> GetAllAsync()
    {
        return await context.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .ToListAsync();
    }

    public async Task<IEnumerable<Booking>> GetByDateRangeAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        return await context.Bookings
            .Where(b => b.PropertyId == propertyId &&
                   b.CheckInDate <= endDate &&
                   b.CheckOutDate >= startDate &&
                   b.Status != BookingStatus.Cancelled)
            .ToListAsync();
    }

    public async Task<bool> IsAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut)
    {
        var conflicting = await context.Bookings
            .AnyAsync(b => b.PropertyId == propertyId &&
                      b.CheckInDate < checkOut &&
                      b.CheckOutDate > checkIn &&
                      b.Status != BookingStatus.Cancelled);
        
        return !conflicting;
    }

    public async Task<Booking> AddAsync(Booking booking)
    {
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();
        return booking;
    }

    public async Task<Booking> UpdateAsync(Booking booking)
    {
        context.Bookings.Update(booking);
        await context.SaveChangesAsync();
        return booking;
    }

    public async Task DeleteAsync(Guid id)
    {
        var booking = await context.Bookings.FindAsync(id);
        if (booking != null)
        {
            context.Bookings.Remove(booking);
            await context.SaveChangesAsync();
        }
    }
}
