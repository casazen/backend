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

    public async Task<bool> IsAvailableAsync(
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        int? directPendingTtlMinutes = null)
    {
        // Normalize to date-only to prevent time-component false conflicts (e.g. same-day turnover).
        // A checkout on Apr 5 at 10:00 and a checkin on Apr 5 at 15:00 is a valid same-day turnover.
        var checkInDate = checkIn.Date;
        var checkOutDate = checkOut.Date;
        var pendingCutoff = directPendingTtlMinutes.HasValue
            ? DateTime.UtcNow.AddMinutes(-directPendingTtlMinutes.Value)
            : (DateTime?)null;

        var conflicting = await context.Bookings
            .AnyAsync(b => b.PropertyId == propertyId &&
                      b.CheckInDate.Date < checkOutDate &&
                      b.CheckOutDate.Date > checkInDate &&
                      b.Status != BookingStatus.Cancelled &&
                      !(pendingCutoff.HasValue &&
                        b.Status == BookingStatus.Pending &&
                        b.Source == BookingSource.Direct &&
                        b.CreatedAt < pendingCutoff.Value));

        return !conflicting;
    }

    public async Task<int> CancelExpiredPendingDirectBookingsAsync(Guid propertyId, int ttlMinutes)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-ttlMinutes);
        var expired = await context.Bookings
            .Where(b => b.PropertyId == propertyId &&
                        b.Status == BookingStatus.Pending &&
                        b.Source == BookingSource.Direct &&
                        b.CreatedAt < cutoff)
            .ToListAsync();

        foreach (var booking in expired)
        {
            booking.Status = BookingStatus.Cancelled;
            booking.UpdatedAt = DateTime.UtcNow;
        }

        if (expired.Count > 0)
            await context.SaveChangesAsync();

        return expired.Count;
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

    public async Task<Booking?> GetByExternalIdAsync(Guid propertyId, string externalId, BookingSource source)
    {
        return await context.Bookings
            .FirstOrDefaultAsync(b => b.PropertyId == propertyId
                && b.ExternalId == externalId
                && b.Source == source);
    }

    public async Task<Booking> UpsertOtaBookingAsync(Booking booking)
    {
        var existing = await GetByExternalIdAsync(booking.PropertyId, booking.ExternalId, booking.Source);
        if (existing != null)
        {
            existing.Status = booking.Status;
            existing.TotalPrice = booking.TotalPrice;
            existing.CheckInDate = booking.CheckInDate;
            existing.CheckOutDate = booking.CheckOutDate;
            existing.GuestId = booking.GuestId;
            existing.NumberOfGuests = booking.NumberOfGuests;
            existing.UpdatedAt = DateTime.UtcNow;
            context.Bookings.Update(existing);
            await context.SaveChangesAsync();
            return existing;
        }

        context.Bookings.Add(booking);
        await context.SaveChangesAsync();
        return booking;
    }
}
